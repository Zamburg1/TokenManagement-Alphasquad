using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Threading.Tasks;

public interface ITwitchChatService
{
    // Core events
    event TwitchIRC.ChatMessageHandlerWithBadges OnMessageReceived;
    
    // Properties
    bool IsConnected { get; }
    string ChannelName { get; }
    string Username { get; }
    
    // Methods
    void SendChatMessage(string message);
    void SetUserColor(string username, string color);
    void SimulateChatMessage(string username, string message);
}

public class TwitchIRC : MonoBehaviour, ITwitchChatService
{
    #region Nested Types
    
    public class TwitchMessageData
    {
        public string Username { get; set; }
        public string Message { get; set; }
        public List<string> Badges { get; set; } = new List<string>();
        public string Color { get; set; } = "#FFFFFF"; // Default color
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
        
        public bool HasBadge(string badgeName) => Badges.Contains(badgeName);
        
        public override string ToString() => 
            $"{Username}{(Badges.Count > 0 ? $" [{string.Join(", ", Badges)}]" : "")}";
    }
    
    private class UserColorInfo 
    {
        public string Color;
        public List<string> Badges = new List<string>();
        public float Timestamp;
        
        public bool IsStale => Time.time - Timestamp > COLOR_CACHE_DURATION;
    }
    
    #endregion
    
    #region Public Events
    
    public delegate void ChatMessageHandlerWithBadges(TwitchMessageData messageData);
    public event ChatMessageHandlerWithBadges OnMessageReceived;
    
    public delegate void UserColorUpdateHandler(string username);
    public event UserColorUpdateHandler OnUserColorUpdated;
    
    #endregion
    
    #region Connection Settings
    
    [Header("Connection Settings")]
    [SerializeField] private string twitchServer = "irc.chat.twitch.tv";
    [SerializeField] private int twitchPort = 6667;
    [SerializeField, Tooltip("How long to wait between reconnection attempts (seconds)")] 
    private float reconnectDelay = 5f;
    [SerializeField, Tooltip("Connection timeout in seconds")]
    private float connectionTimeout = 10f;
    [SerializeField, Tooltip("TCP connection requires regular PING messages to keep the connection alive")]
    private float pingInterval = 300f; // 5 minutes as recommended by Twitch
    
    private TcpClient twitchClient;
    private StreamReader reader;
    private StreamWriter writer;
    private Thread readerThread;
    private CancellationTokenSource cancellationTokenSource;
    
    private bool isConnected = false;
    private bool shouldReconnect = true;
    
    #endregion
    
    #region User Colors and Badges
    
    [Header("Role Colors")]
    [SerializeField] private string _broadcasterColor = "#E71212";
    [SerializeField] private string _moderatorColor = "#00AD03";
    [SerializeField] private string _vipColor = "#E005E0";
    [SerializeField] private string _artistColor = "#0099FF";
    [SerializeField] private string _subscriberColor = "#C79800";
    [SerializeField] private string _cheererColor = "#9D6AFF";
    [SerializeField] private string _defaultUserColor = "#FFFFFF";
    
    // Color cache dictionary to store username -> color mappings
    private Dictionary<string, UserColorInfo> userColorCache = new Dictionary<string, UserColorInfo>(StringComparer.OrdinalIgnoreCase);
    private const float COLOR_CACHE_DURATION = 60f; // Cache roles for just 1 minute to ensure changes reflect quickly
    
    // Public properties to access role colors
    public string BroadcasterColor => _broadcasterColor;
    public string ModeratorColor => _moderatorColor;
    public string VipColor => _vipColor;
    public string ArtistColor => _artistColor;
    public string SubscriberColor => _subscriberColor;
    public string CheererColor => _cheererColor;
    public string DefaultUserColor => _defaultUserColor;
    
    #endregion
    
    #region Debug and Other Settings
    
    [Header("Debug Options")]
    [SerializeField, Tooltip("Show debug messages in console")]
    private bool showDebugMessages = false;
    [SerializeField, Tooltip("Only log command messages (those starting with !)")]
    private bool onlyLogCommands = true;
    [SerializeField, Tooltip("Enable verbose message processing logging")]
    private bool verboseMessageLogging = false;
    
    [Header("Auto-Reconnect")]
    [SerializeField, Tooltip("Automatically try to reconnect if credentials become available")]
    private bool autoReconnectEnabled = true;
    [SerializeField, Tooltip("How often to check for new credentials (seconds)")]
    private float autoReconnectCheckInterval = 60f;
    private float lastReconnectCheck = 0f;
    
    #endregion
    
    #region Public Properties
    
    // Credentials (all come from TokenManager)
    private string channelName;
    private string username;
    private string oauthToken;
    
    // Public properties
    public bool IsConnected => isConnected;
    public string ChannelName => channelName;
    public string Username => username;
    
    // Check if connection is ready for operations
    public bool IsConnectionReady => isConnected && writer != null && twitchClient?.Connected == true;
    
    #endregion
    
    #region Simplified Message Buffer
    
    // Simple message buffer
    private readonly Queue<TwitchMessageData> messageBuffer = new Queue<TwitchMessageData>(50);
    private const int MAX_BUFFER_SIZE = 200;
    private float messageBufferStartTime = 0f;
    private bool isProcessingBuffer = false;
    private bool hasRegisteredHandlers = false;
    private bool hadHandlersRegistered = false;
    private const float messageBufferMaxTime = 30f; // Buffer timeout
    private bool wasEverConnected = false;
    private int totalMessagesReceived = 0;
    private float lastMessageReceivedTime = 0f;
    private int failedMessageDeliveries = 0;
    
    // Simple buffer message method
    private void BufferMessage(TwitchMessageData messageData)
    {
        if (messageData == null) return;
        
        if (messageBufferStartTime == 0f)
        {
            messageBufferStartTime = Time.time;
            Debug.Log("[TwitchIRC] Started message buffering");
        }
        
        // Remove oldest message if buffer is full
        if (messageBuffer.Count >= MAX_BUFFER_SIZE)
        {
            messageBuffer.Dequeue();
        }
        
        messageBuffer.Enqueue(messageData);
    }

    // Process buffered messages
    private void ProcessMessageBuffer()
    {
        if (isProcessingBuffer || messageBuffer.Count == 0) return;
        
        isProcessingBuffer = true;
        Debug.Log($"[TwitchIRC] Processing {messageBuffer.Count} buffered messages");
        
        try
        {
            int count = messageBuffer.Count;
            for (int i = 0; i < count; i++)
            {
                if (messageBuffer.Count == 0) break;
                
                var message = messageBuffer.Dequeue();
                SafeInvokeMessage(message, false);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TwitchIRC] Error processing buffer: {ex.Message}");
        }
        
        isProcessingBuffer = false;
        messageBufferStartTime = 0f;
    }

    // Process a message - buffer if handlers not ready
    private void ProcessChatMessage(TwitchMessageData messageData)
    {
        if (hasRegisteredHandlers && OnMessageReceived != null)
        {
            SafeInvokeMessage(messageData, false);
        }
        else
        {
            BufferMessage(messageData);
        }
    }

    #endregion

    #region Simplified Thread Management

    // Start reader thread
    private void StartReaderThread()
    {
        StopReaderThread();
        
        try
        {
            cancellationTokenSource = new CancellationTokenSource();
            readerThread = new Thread(() => ReadMessages(cancellationTokenSource.Token));
            readerThread.IsBackground = true;
            readerThread.Start();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TwitchIRC] Failed to start reader thread: {ex.Message}");
        }
    }

    // Stop reader thread
    private void StopReaderThread()
    {
        if (cancellationTokenSource != null)
        {
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
            cancellationTokenSource = null;
        }
        
        if (readerThread?.IsAlive == true)
        {
            bool threadStopped = readerThread.Join(2000);
            if (!threadStopped)
            {
                Debug.LogWarning("[TwitchIRC] Reader thread did not exit within timeout");
            }
        }
        
        readerThread = null;
    }

    // The main message reading method
    private void ReadMessages(CancellationToken cancellationToken)
    {
        Debug.Log("[TwitchIRC] Reader thread started");
        
        while (!cancellationToken.IsCancellationRequested && twitchClient?.Connected == true && reader != null)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested) break;
                
                string message = reader.ReadLine();
                
                if (message == null) break;
                
                // Process message on main thread
                UnityMainThreadDispatcher.Instance().Enqueue(() => ProcessMessage(message));
            }
            catch (IOException)
            {
                // Connection closed or error
                if (!cancellationToken.IsCancellationRequested && shouldReconnect)
                {
                    UnityMainThreadDispatcher.Instance().Enqueue(() => CleanupConnection(true));
                }
                break;
            }
            catch (ObjectDisposedException)
            {
                // Reader was disposed
                break;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TwitchIRC] Error in reader thread: {ex.Message}");
                
                if (!cancellationToken.IsCancellationRequested)
                {
                    UnityMainThreadDispatcher.Instance().Enqueue(() => CleanupConnection(true));
                }
                break;
            }
        }
        
        Debug.Log("[TwitchIRC] Reader thread exiting");
    }

    #endregion

    #region Core Message Handling

    // Send ping to keep connection alive
    private void SendPing()
    {
        if (!IsConnectionReady) return;
        
        try
        {
            writer.WriteLine("PING :tmi.twitch.tv");
        }
        catch
        {
            CleanupConnection(true);
        }
    }

    // Handle incoming message
    private void ProcessMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        
        try
        {
            // Handle PING messages from server
            if (message.StartsWith("PING"))
            {
                writer.WriteLine("PONG :tmi.twitch.tv");
                return;
            }
            
            // Only process chat messages
            if (!message.Contains("PRIVMSG")) return;
            
            // Parse the message
            var (tagSection, remainingMessage) = ParseTags(message);
            Dictionary<string, string> tags = ParseTagsIntoDictionary(tagSection);
            
            // Extract username and message content
            if (!TryParsePrivateMessage(remainingMessage, out string username, out string chatMessage))
            {
                return;
            }
            
            // Only process commands (messages starting with !)
            if (!chatMessage.StartsWith("!")) return;
            
            // Extract badges and color
            List<string> badges = ParseBadges(tags);
            string color = ParseColor(tags);
            
            // Create message data object
            TwitchMessageData messageData = new TwitchMessageData
            {
                Username = username,
                Message = chatMessage,
                Badges = badges,
                Color = color,
                Tags = tags
            };
            
            // Process or buffer the message
            ProcessChatMessage(messageData);
        }
        catch (Exception e)
        {
            Debug.LogError($"[TwitchIRC] Message processing error: {e.Message}");
        }
    }

    // Safely invoke message handlers
    private void SafeInvokeMessage(TwitchMessageData messageData, bool logDetails = false)
    {
        if (OnMessageReceived == null) return;
        
        try
        {
            OnMessageReceived.Invoke(messageData);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TwitchIRC] Message handler error: {ex.Message}");
        }
    }

    #endregion

    #region Message Parsing Helpers
    
    private (string tagSection, string remainingMessage) ParseTags(string message)
    {
        string tagSection = "";
        
        if (message.StartsWith('@'))
        {
            int spaceIndex = message.IndexOf(' ');
            if (spaceIndex > 0)
            {
                tagSection = message.Substring(1, spaceIndex - 1);
                message = message.Substring(spaceIndex + 1);
            }
        }
        
        return (tagSection, message);
    }
    
    private Dictionary<string, string> ParseTagsIntoDictionary(string tagSection)
    {
        Dictionary<string, string> tags = new Dictionary<string, string>();
        
        if (string.IsNullOrEmpty(tagSection)) return tags;
        
        string[] tagPairs = tagSection.Split(';');
        foreach (string tagPair in tagPairs)
        {
            string[] keyValue = tagPair.Split('=');
            if (keyValue.Length == 2)
            {
                tags[keyValue[0]] = keyValue[1];
            }
        }
        
        return tags;
    }
    
    private bool TryParsePrivateMessage(string message, out string username, out string chatMessage)
    {
        username = null;
        chatMessage = null;
        
        // Check for PRIVMSG (chat messages)
        int privMsgIndex = message.IndexOf("PRIVMSG #");
        if (privMsgIndex < 0) return false;
            
        // Extract username (between the first ! and first space)
        int exclamationIndex = message.IndexOf('!');
        if (exclamationIndex <= 0) return false;
            
        username = message.Substring(1, exclamationIndex - 1); // Skip the colon at the start
        
        // Extract the message content (after the channel name)
        int channelEndIndex = message.IndexOf(':', privMsgIndex);
        if (channelEndIndex < 0) return false;
            
        chatMessage = message.Substring(channelEndIndex + 1);
        return true;
    }
    
    private List<string> ParseBadges(Dictionary<string, string> tags)
    {
        List<string> badges = new List<string>();
        
        if (tags.TryGetValue("badges", out string badgesValue) && !string.IsNullOrEmpty(badgesValue))
        {
            string[] badgePairs = badgesValue.Split(',');
            foreach (string badgePair in badgePairs)
            {
                int versionSeparator = badgePair.IndexOf('/');
                if (versionSeparator > 0)
                {
                    badges.Add(badgePair.Substring(0, versionSeparator));
                }
                else if (!string.IsNullOrEmpty(badgePair))
                {
                    badges.Add(badgePair);
                }
            }
        }
        
        return badges;
    }
    
    private string ParseColor(Dictionary<string, string> tags)
    {
        if (tags.TryGetValue("color", out string colorValue) && !string.IsNullOrEmpty(colorValue))
        {
            return colorValue;
        }
        
        return _defaultUserColor;
    }
    
    #endregion
    
    #region Color and Badge Management
    
    // Get appropriate color for a user based on their badges
    public string GetUserColor(TwitchMessageData messageData)
    {
        if (messageData == null) return _defaultUserColor;
        
        // Cache this user's color and badges
        CacheUserColor(messageData.Username, messageData.Color, messageData.Badges);
        
        // Role hierarchy (highest to lowest)
        if (messageData.HasBadge("broadcaster")) return _broadcasterColor;
        if (messageData.HasBadge("moderator")) return _moderatorColor;
        if (messageData.HasBadge("vip")) return _vipColor;
        if (messageData.HasBadge("artist")) return _artistColor;
        if (messageData.HasBadge("subscriber")) return _subscriberColor;
        if (messageData.HasBadge("bits") || messageData.HasBadge("bits-leader")) return _cheererColor;
        
        // Get color from tags if present, otherwise use default color
        return string.IsNullOrEmpty(messageData.Color) ? _defaultUserColor : messageData.Color;
    }
    
    // Get user color by username (for components that don't have message data)
    public string GetUserColorByUsername(string username)
    {
        if (string.IsNullOrEmpty(username)) return _defaultUserColor;
        
        // Check cache first
        string usernameKey = username.ToLowerInvariant();
        if (userColorCache.TryGetValue(usernameKey, out UserColorInfo colorInfo) && !colorInfo.IsStale)
        {
            // Use cached badge information to determine color
            if (colorInfo.Badges.Contains("broadcaster")) return _broadcasterColor;
            if (colorInfo.Badges.Contains("moderator")) return _moderatorColor;
            if (colorInfo.Badges.Contains("vip")) return _vipColor;
            if (colorInfo.Badges.Contains("artist")) return _artistColor;
            if (colorInfo.Badges.Contains("subscriber")) return _subscriberColor;
            if (colorInfo.Badges.Contains("bits") || colorInfo.Badges.Contains("bits-leader")) return _cheererColor;
            
            // Use cached color or default
            return string.IsNullOrEmpty(colorInfo.Color) ? _defaultUserColor : colorInfo.Color;
        }
        
        // If not in cache, return default and we'll update later when they chat
        return _defaultUserColor;
    }
    
    // Store user color in cache
    private void CacheUserColor(string username, string color, List<string> badges)
    {
        if (string.IsNullOrEmpty(username)) return;
        
        string usernameKey = username.ToLowerInvariant();
        
        // Create or update cache entry
        if (!userColorCache.TryGetValue(usernameKey, out UserColorInfo colorInfo))
        {
            colorInfo = new UserColorInfo();
            userColorCache[usernameKey] = colorInfo;
        }
        
        // Update cache information
        colorInfo.Color = color;
        colorInfo.Badges = new List<string>(badges); // Make a copy to avoid reference issues
        colorInfo.Timestamp = Time.time;
        
        // If this is a chat message from the user, we always have the freshest data
        // So immediately update any UI that might use this user's color
        OnUserColorUpdated?.Invoke(username);
    }
    
    // Optimize color cache management with efficient lookups
    private void CleanupColorCache()
    {
        // Only run cleanup periodically to reduce overhead
        if (Time.frameCount % 3600 != 0) return; // ~60 seconds at 60 FPS
        
        int initialCount = userColorCache.Count;
        
        if (initialCount == 0) return; // Skip processing if cache is empty
        
        List<string> keysToRemove = new List<string>(initialCount / 4); // Pre-allocate for efficiency
        float currentTime = Time.time; // Cache the current time
        
        foreach (var entry in userColorCache)
        {
            if (currentTime - entry.Value.Timestamp > COLOR_CACHE_DURATION)
            {
                keysToRemove.Add(entry.Key);
            }
        }
        
        if (keysToRemove.Count > 0)
        {
            foreach (var key in keysToRemove)
            {
                userColorCache.Remove(key);
            }
            
            if (verboseMessageLogging)
            {
                Debug.Log($"[TwitchIRC] Cleaned up color cache: removed {keysToRemove.Count} entries, {userColorCache.Count} remaining");
            }
        }
    }
    
    #endregion
    
    #region Connection Management

    // Connection retry parameters
    private int connectionAttempts = 0;
    private const int maxConnectionAttempts = 5;
    private float[] retryDelays = new float[] { 3f, 5f, 10f, 20f, 30f }; // Increasing backoff times
    private float lastConnectionAttemptTime = 0f;
    private bool isReconnecting = false;
    
    // Update AttemptReconnect to use the reconnectDelay field
    private void AttemptReconnect()
    {
        if (isReconnecting) return;
        
        isReconnecting = true;
        
        // Close existing connection first
        CleanupConnection();
        
        // Use the reconnectDelay field from the inspector
        float delayToUse = reconnectDelay;
        
        // Apply additional delay based on attempt count to implement backoff
        if (connectionAttempts > 0)
        {
            // Add increasing delay (but cap at 30 seconds)
            delayToUse = Mathf.Min(reconnectDelay * (1 + connectionAttempts * 0.5f), 30f);
        }
        
        Debug.Log($"[TwitchIRC] Connection issue detected. Scheduling reconnect in {delayToUse} seconds (attempt {connectionAttempts + 1}/{maxConnectionAttempts})");
        
        // Schedule reconnection
        StartCoroutine(ReconnectAfterDelay(delayToUse));
    }
    
    private IEnumerator ReconnectAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        
        Debug.Log("[TwitchIRC] Attempting to reconnect to Twitch...");
        
        // Reset necessary state
        isConnected = false;
        
        // Increment connection attempts
        connectionAttempts++;
        lastConnectionAttemptTime = Time.time;
        
        bool reconnectSuccess = false;
        
        try
        {
            ConnectToTwitch();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TwitchIRC] Error during reconnect: {ex.Message}");
            reconnectSuccess = false;
        }
        
        // Check if connection was successful after a short delay
        yield return new WaitForSeconds(3f);
        
        if (isConnected)
        {
            Debug.Log("[TwitchIRC] Reconnect successful!");
            connectionAttempts = 0; // Reset the counter on success
            reconnectSuccess = true;
        }
        
        // Handle reconnection failure outside the try/catch
        if (!reconnectSuccess)
        {
            if (connectionAttempts < maxConnectionAttempts)
            {
                Debug.LogWarning($"[TwitchIRC] Reconnect failed, will try again (attempt {connectionAttempts}/{maxConnectionAttempts})");
                AttemptReconnect(); // Try again with increasing delay
            }
            else
            {
                Debug.LogError("[TwitchIRC] Maximum reconnection attempts reached. Please check your network connection.");
                // Notify game systems about permanent connection failure
                if (OnConnectionFailed != null)
                {
                    OnConnectionFailed.Invoke("Maximum reconnection attempts reached");
                }
            }
        }
        
        isReconnecting = false;
    }
    
    // Implement a connection failed event
    public event Action<string> OnConnectionFailed;
    
    #endregion
    
    #region Unity Lifecycle
    
    private void Start()
    {
        // Make sure this object persists across scene loads
        DontDestroyOnLoad(gameObject);
        
        // Register with service locator
        TwitchChatServiceLocator.Instance.RegisterPrimaryService(this);
        
        Initialize();
    }
    
    private void Update()
    {
        CleanupColorCache();
        
        // First, process any due timers
        ProcessTimers();
        
        // Check for new credentials if not connected
        if (autoReconnectEnabled && connectionState == ConnectionState.Disconnected)
        {
            if (Time.time - lastReconnectCheck > autoReconnectCheckInterval)
            {
                lastReconnectCheck = Time.time;
                CheckAndReconnect();
            }
        }
        
        // Auto-register handlers if we have messages but no handlers yet
        if (messageBuffer.Count > 0 && OnMessageReceived != null && 
            OnMessageReceived.GetInvocationList().Length > 0 && !hasRegisteredHandlers)
        {
            LogInfo("Auto-registering handlers due to buffered messages");
            NotifyHandlersRegistered();
        }
        
        // Check message buffer timeout
        if (messageBufferStartTime > 0 && Time.time - messageBufferStartTime > messageBufferMaxTime && !isProcessingBuffer)
        {
            if (messageBuffer.Count > 0)
            {
                Debug.Log($"[TwitchIRC] Message buffer timeout reached with {messageBuffer.Count} messages");
                ProcessMessageBuffer();
            }
            else
            {
                messageBufferStartTime = 0f;
            }
        }
        
        // Send heartbeat ping to check connection health
        if (isConnected && Time.time - lastPingSentTime > PING_INTERVAL)
        {
            SendHeartbeat();
        }
        
        // Check for ping timeout
        if (isPingPending && Time.time - lastPingSentTime > PONG_TIMEOUT)
        {
            Debug.LogWarning("[TwitchIRC] PING timeout - no response received. Connection might be stale.");
            isPingPending = false; // Reset the flag
            AttemptReconnect();    // Try to reconnect
        }
        
        // Log status periodically (once every 10 seconds)
        if (Time.time - lastStatusLogTime >= 10f)
        {
            lastStatusLogTime = Time.time;
            LogConnectionStatus();
        }
        
        // Check for lost connection (no messages for a long time)
        if (isConnected && wasEverConnected && totalMessagesReceived > 0 && 
            Time.time - lastMessageReceivedTime > 300f) // 5 minutes with no messages
        {
            Debug.LogWarning("[TwitchIRC] Potential connection issue: No messages received for 5 minutes");
            AttemptReconnect();
        }
    }
    
    private void OnDisable()
    {
        Debug.Log($"[TwitchIRC] OnDisable called, disconnecting from Twitch");
        DisconnectFromTwitch(false);
    }
    
    private void OnDestroy()
    {
        Debug.Log($"[TwitchIRC] OnDestroy called, performing final cleanup");
        
        // Ensure we clean up the connection
        shouldReconnect = false;
        DisconnectFromTwitch(false);
        StopReaderThread();
    }
    
    #endregion
    
    #region Initialization and Connection
    
    public void Initialize()
    {
        if (TokenManager.Instance == null)
        {
            Debug.LogError("[TwitchIRC] TokenManager not found");
            return;
        }
        
        LoadCredentials();
        
        // Check if mock mode is active via the service locator
        bool isMockModeActive = TwitchChatServiceLocator.Instance.IsMockModeActive();
        
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(oauthToken) && !string.IsNullOrEmpty(channelName))
        {
            ConnectToTwitch();
        }
        else
        {
            // More specific log messages
            if (isMockModeActive)
            {
                Debug.Log("<color=yellow>[TwitchIRC] Running in mock mode. Real Twitch connection skipped.</color>");
            }
            else if (string.IsNullOrEmpty(username))
            {
                Debug.LogWarning("[TwitchIRC] Missing Twitch username. Set via TokenManager.");
            }
            else if (string.IsNullOrEmpty(oauthToken))
            {
                Debug.LogWarning("[TwitchIRC] Missing Twitch OAuth token. Generate via TokenManager.");
            }
            else if (string.IsNullOrEmpty(channelName))
            {
                Debug.LogWarning("[TwitchIRC] Missing Twitch channel name. Set via TokenManager.");
            }
            
            // Set up dummy values for testing/mock mode
            if (string.IsNullOrEmpty(username)) username = "MockUser";
            if (string.IsNullOrEmpty(channelName)) channelName = "MockChannel";
        }
        
        // Reset message buffer state
        messageBuffer.Clear();
        messageBufferStartTime = 0f;
        
        // Log handler count during initialization
        if (OnMessageReceived != null)
        {
            int handlerCount = OnMessageReceived.GetInvocationList().Length;
            Debug.Log($"[TwitchIRC] Initialize found {handlerCount} message handlers");
            
            // Auto-register if handlers already exist
            if (handlerCount > 0 && !hasRegisteredHandlers)
            {
                Debug.Log("[TwitchIRC] Auto-registering existing handlers during initialization");
                NotifyHandlersRegistered();
            }
        }
        else
        {
            Debug.Log("[TwitchIRC] Initialize: No message handlers registered yet");
        }
    }
    
    private void LoadCredentials()
    {
        TokenManager tokenManager = TokenManager.Instance;
        if (tokenManager == null)
        {
            Debug.LogError("[TwitchIRC] TokenManager not found");
            return;
        }
        
        oauthToken = tokenManager.GetTwitchOAuthFormatted();
        username = tokenManager.GetTwitchUsername();
        channelName = tokenManager.GetTwitchChannel();
        
        // Format OAuth token if needed
        if (!string.IsNullOrEmpty(oauthToken) && !oauthToken.StartsWith("oauth:"))
        {
            oauthToken = "oauth:" + oauthToken;
        }
    }
    
    // Removed duplicate DisconnectFromTwitch method
    
    #endregion
    
    #region Connection Maintenance
    
    // Track connection status for better handling of unstable connections
    private int successfulPingCount = 0;
    private int failedPingCount = 0;
    private bool isConnectionStable = false;
    private float lastStableConnectionTime = 0f;
    private float connectionQualityCheckTime = 0f;
    private const float CONNECTION_QUALITY_CHECK_INTERVAL = 60f; // Check connection quality every minute

    // New method to assess connection quality based on success/failure rates
    private void CheckConnectionQuality()
    {
        connectionQualityCheckTime = Time.time;
        
        // Calculate ping success rate
        int totalPings = successfulPingCount + failedPingCount;
        float successRate = totalPings > 0 ? (float)successfulPingCount / totalPings : 0f;
        
        bool wasStable = isConnectionStable;
        
        // Consider connection stable if success rate is high enough
        isConnectionStable = successRate >= 0.8f && totalMessagesReceived > 10;
        
        // Log connection quality status
        if (verboseMessageLogging || wasStable != isConnectionStable)
        {
            Debug.Log($"[TwitchIRC] Connection quality: {successRate:P2} success rate " +
                      $"({successfulPingCount}/{totalPings} pings, {totalMessagesReceived} messages received) " +
                      $"Status: {(isConnectionStable ? "STABLE" : "UNSTABLE")}");
        }
        
        // Reset counters periodically to adapt to changing network conditions
        if (totalPings > 100)
        {
            successfulPingCount = (int)(successfulPingCount * 0.5f);  // Keep half the history
            failedPingCount = (int)(failedPingCount * 0.5f);
        }
        
        // Update last stable time if connection is good
        if (isConnectionStable)
        {
            lastStableConnectionTime = Time.time;
        }
        // If connection has been unstable for too long, try to reconnect
        else if (wasStable && Time.time - lastStableConnectionTime > 180f)  // 3 minutes of instability
        {
            Debug.LogWarning("[TwitchIRC] Connection has been unstable for too long, initiating reconnect");
            AttemptReconnect();
        }
    }

    private void SendPong()
    {
        if (writer == null) return;
        
        try
        {
            writer.WriteLine("PONG :tmi.twitch.tv");
        }
        catch
        {
            isConnected = false;
            UnityMainThreadDispatcher.Instance().Enqueue(() => StartCoroutine(ReconnectAfterDelay(3f)));
        }
    }
    
    #endregion
    
    // Check for valid credentials and attempt to connect
    private void CheckAndReconnect()
    {
        if (isConnected) return;
        
        // Refresh credentials
        LoadCredentials();
        
        // Check if we now have valid credentials
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(oauthToken) && !string.IsNullOrEmpty(channelName))
        {
            Debug.Log("[TwitchIRC] Valid credentials detected, attempting to connect");
            ConnectToTwitch();
        }
    }
    
    // Add this method to set a user's color (especially for mock users)
    public void SetUserColor(string username, string color)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(color))
            return;
            
        // Store the color in the cache with empty badges
        CacheUserColor(username, color, new List<string>());
    }
    
    // Add this method to notify when handlers are registered
    public void NotifyHandlersRegistered()
    {
        hasRegisteredHandlers = true;
        hadHandlersRegistered = true;
        
        // Log handler count
        int handlerCount = OnMessageReceived?.GetInvocationList().Length ?? 0;
        LogInfo($"Handlers explicitly registered. Current handler count: {handlerCount}");
        
        ProcessMessageBuffer();
    }
    
    // Add a new field to track last log time
    private float lastStatusLogTime = 0f;

    // Add an improved version of LogConnectionStatus to include connection quality metrics
    private void LogConnectionStatus()
    {
        string threadStatus = readerThread?.IsAlive == true 
            ? $"ALIVE (ID: {readerThread.ManagedThreadId})" 
            : "NOT RUNNING";
            
        bool tcpConnected = twitchClient?.Connected == true;
        int handlerCount = OnMessageReceived?.GetInvocationList().Length ?? 0;
        
        // Calculate basic metrics
        float messageRate = 0f;
        if (wasEverConnected && lastMessageReceivedTime > 0)
        {
            float timeConnected = Mathf.Max(1f, Time.time - lastStableConnectionTime);
            messageRate = totalMessagesReceived / timeConnected;
        }
        
        string statusMessage = $"Status Report - State: {connectionState}, " +
                             $"TCP: {tcpConnected}, " +
                             $"Thread: {threadStatus}, " +
                             $"Handlers: {handlerCount}, " +
                             $"Buffer: {messageBuffer.Count}, " +
                             $"Msgs: {totalMessagesReceived} ({messageRate:F1}/s), " +
                             $"Failed: {failedMessageDeliveries}, " +
                             $"Connection: {(isConnectionStable ? "STABLE" : "UNSTABLE")}";
        
        LogInfo(statusMessage);
              
        // Hard check of event subscription to detect dropped events
        bool hasMessageHandlers = (OnMessageReceived != null && OnMessageReceived.GetInvocationList().Length > 0);
        if (!hasMessageHandlers && hasRegisteredHandlers)
        {
            LogError("CRITICAL ERROR: Message handlers were registered but now they're gone! Resetting registration state.");
            hasRegisteredHandlers = hadHandlersRegistered;
        }
    }

    // Add heartbeat system for connection health monitoring
    private float lastPingSentTime = 0f;
    private bool isPingPending = false;
    private const float PING_INTERVAL = 60f; // 1 minute
    private const float PONG_TIMEOUT = 30f;  // 30 seconds to respond

    // Improved heartbeat system
    private void SendHeartbeat()
    {
        if (!isConnected || writer == null) return;
        
        try
        {
            writer.WriteLine("PING :tmi.twitch.tv");
            lastPingSentTime = Time.time;
            isPingPending = true;
            
            if (verboseMessageLogging)
            {
                Debug.Log($"[TwitchIRC] Sent heartbeat PING at {lastPingSentTime}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TwitchIRC] Error sending heartbeat: {ex.Message}");
            AttemptReconnect();
        }
    }

    // Add proper resource cleanup on application exit
    private void OnApplicationQuit()
    {
        Debug.Log("[TwitchIRC] Application quitting, cleaning up resources");
        shouldReconnect = false;
        DisconnectFromTwitch(false);
        messageBuffer.Clear();
        userColorCache.Clear();
        CancelInvoke();
    }

    #region Testing and Simulation

    // Add this method to simulate a chat message for testing purposes
    public void SimulateChatMessage(string username, string message)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(message))
            return;
        
        // Create message data with default values for a test user
        TwitchMessageData mockData = new TwitchMessageData
        {
            Username = username,
            Message = message,
            Color = "#FF7F50", // Coral color for easy identification
            Badges = new List<string>() // No badges for mock users
        };
        
        // Fire the message received event
        Debug.Log($"[TwitchIRC] Simulating message: {username}: {message}");
        
        // Check for registered handlers
        if (OnMessageReceived != null)
        {
            OnMessageReceived.Invoke(mockData);
        }
        else
        {
            Debug.LogWarning($"[TwitchIRC] Cannot simulate message: no handlers registered");
            // Buffer the message for later delivery
            BufferMessage(mockData);
        }
    }

    #endregion

    #region Unified Connection Management

    // Simple connection states
    public enum ConnectionState 
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting
    }

    // Connection state tracking
    private ConnectionState connectionState = ConnectionState.Disconnected;

    // Update connection state
    private void UpdateConnectionState(ConnectionState newState)
    {
        if (connectionState == newState) return;
        
        Debug.Log($"[TwitchIRC] Connection state: {connectionState} → {newState}");
        connectionState = newState;
        
        // Update isConnected flag for backward compatibility
        isConnected = (newState == ConnectionState.Connected);
    }

    // Attempt to connect to Twitch
    private void ConnectToTwitch()
    {
        // Don't connect if already connected or in the process of connecting
        if (connectionState == ConnectionState.Connected || 
            connectionState == ConnectionState.Connecting)
        {
            return;
        }
        
        UpdateConnectionState(ConnectionState.Connecting);
        
        // Clean up existing connection first
        CleanupConnection(false);
        
        try
        {
            // Create and connect TCP client
            twitchClient = new TcpClient();
            
            // Use a timeout to avoid hanging
            int timeoutMs = Mathf.Max(1000, (int)(connectionTimeout * 1000));
            IAsyncResult result = twitchClient.BeginConnect(twitchServer, twitchPort, null, null);
            bool success = result.AsyncWaitHandle.WaitOne(timeoutMs);
            
            if (!success)
            {
                twitchClient.Close();
                Debug.LogError($"[TwitchIRC] Connection timeout after {connectionTimeout} seconds");
                UpdateConnectionState(ConnectionState.Disconnected);
                
                if (shouldReconnect)
                {
                    AttemptReconnect();
                }
                return;
            }
            
            twitchClient.EndConnect(result);
            
            // Set up stream reader/writer
            var networkStream = twitchClient.GetStream();
            reader = new StreamReader(networkStream);
            writer = new StreamWriter(networkStream) { NewLine = "\r\n", AutoFlush = true };
            
            // Send authentication
            string formattedOAuth = oauthToken.StartsWith("oauth:") ? oauthToken : "oauth:" + oauthToken;
            writer.WriteLine($"PASS {formattedOAuth}");
            writer.WriteLine($"NICK {username.ToLower()}");
            writer.WriteLine($"USER {username} 8 * :{username}");
            
            // Request capabilities for tags and commands
            writer.WriteLine("CAP REQ :twitch.tv/tags");
            writer.WriteLine("CAP REQ :twitch.tv/commands");
            
            // Join channel
            writer.WriteLine($"JOIN #{channelName.ToLower()}");
            
            // Start reader thread
            StartReaderThread();
            
            // Start ping timer
            InvokeRepeating(nameof(SendPing), pingInterval, pingInterval);
            
            // Update state
            UpdateConnectionState(ConnectionState.Connected);
            
            Debug.Log($"[TwitchIRC] Connected to Twitch chat as {username} in channel {channelName}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TwitchIRC] Connection error: {e.Message}");
            CleanupConnection(shouldReconnect);
        }
    }

    // Clean up connection
    private void CleanupConnection(bool triggerReconnect = false)
    {
        // Cancel the ping timer
        CancelInvoke(nameof(SendPing));
        
        // Update state
        if (connectionState != ConnectionState.Disconnected)
            UpdateConnectionState(ConnectionState.Disconnected);
        
        // Stop reader thread
        StopReaderThread();
        
        // Close resources
        try
        {
            if (writer != null)
            {
                writer.Close();
                writer = null;
            }
            
            if (reader != null)
            {
                reader.Close();
                reader = null;
            }
            
            if (twitchClient != null)
            {
                twitchClient.Close();
                twitchClient = null;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TwitchIRC] Error during cleanup: {ex.Message}");
        }
        
        // Attempt reconnect if requested
        if (triggerReconnect && shouldReconnect && !isReconnecting)
        {
            AttemptReconnect();
        }
    }

    // Disconnect method
    public void DisconnectFromTwitch(bool attemptReconnect = true)
    {
        Debug.Log($"[TwitchIRC] Disconnecting from Twitch (reconnect: {attemptReconnect})");
        shouldReconnect = attemptReconnect;
        CleanupConnection(attemptReconnect);
    }

    #endregion

    #region Unified Logging System

    // Log levels
    private enum LogLevel { Debug, Info, Warning, Error }

    // Unified logging method with automatic filtering
    private void LogMessage(string message, LogLevel level = LogLevel.Info, bool isCommandRelated = false)
    {
        // Skip debug messages unless verbose logging is enabled
        if (level == LogLevel.Debug && !verboseMessageLogging)
            return;
        
        // Skip command messages if we're filtering them out
        if (isCommandRelated && showDebugMessages && onlyLogCommands)
            return;
        
        // Format message with proper prefix
        string formattedMessage = $"[TwitchIRC] {message}";
        
        // Log with appropriate level
        switch (level)
        {
            case LogLevel.Debug:
            case LogLevel.Info:
                Debug.Log(formattedMessage);
                break;
            case LogLevel.Warning:
                Debug.LogWarning(formattedMessage);
                break;
            case LogLevel.Error:
                Debug.LogError(formattedMessage);
                break;
        }
    }

    // Convenience logging methods
    private void LogDebug(string message, bool isCommandRelated = false)
    {
        LogMessage(message, LogLevel.Debug, isCommandRelated);
    }

    private void LogInfo(string message, bool isCommandRelated = false)
    {
        LogMessage(message, LogLevel.Info, isCommandRelated);
    }

    private void LogWarning(string message)
    {
        LogMessage(message, LogLevel.Warning);
    }

    private void LogError(string message)
    {
        LogMessage(message, LogLevel.Error);
    }

    #endregion

    #region Unified Timer Management

    // Timer tracking
    private class TimerInfo
    {
        public float LastRunTime { get; set; } = 0f;
        public float Interval { get; set; }
        public string Name { get; set; }
        public bool IsEnabled { get; set; } = true;
        
        public TimerInfo(string name, float interval)
        {
            Name = name;
            Interval = interval;
            LastRunTime = Time.time;
        }
        
        public bool ShouldRun(float currentTime)
        {
            return IsEnabled && (currentTime - LastRunTime >= Interval);
        }
        
        public void MarkRun()
        {
            LastRunTime = Time.time;
        }
    }

    // Timer collection
    private readonly Dictionary<string, TimerInfo> timers = new Dictionary<string, TimerInfo>();

    // Timer names
    private const string TIMER_PING = "ping";
    private const string TIMER_CONNECTION_CHECK = "connectionCheck";
    private const string TIMER_BUFFER_CHECK = "bufferCheck";
    private const string TIMER_STATUS_LOG = "statusLog";
    private const string TIMER_QUALITY_CHECK = "qualityCheck";

    // Initialize timers
    private void InitializeTimers()
    {
        timers.Clear();
        
        // Add default timers
        timers.Add(TIMER_PING, new TimerInfo(TIMER_PING, pingInterval));
        timers.Add(TIMER_CONNECTION_CHECK, new TimerInfo(TIMER_CONNECTION_CHECK, 60f)); // 1 minute
        timers.Add(TIMER_BUFFER_CHECK, new TimerInfo(TIMER_BUFFER_CHECK, 15f));  // 15 seconds
        timers.Add(TIMER_STATUS_LOG, new TimerInfo(TIMER_STATUS_LOG, 60f));  // 1 minute
        timers.Add(TIMER_QUALITY_CHECK, new TimerInfo(TIMER_QUALITY_CHECK, 120f)); // 2 minutes
    }

    // Run timers that are due
    private void ProcessTimers()
    {
        float currentTime = Time.time;
        
        foreach (var timer in timers.Values)
        {
            if (timer.ShouldRun(currentTime))
            {
                try
                {
                    RunTimerAction(timer.Name);
                    timer.MarkRun();
                }
                catch (Exception ex)
                {
                    LogError($"Error running timer {timer.Name}: {ex.Message}");
                }
            }
        }
    }

    // Execute the appropriate action for each timer
    private void RunTimerAction(string timerName)
    {
        switch (timerName)
        {
            case TIMER_PING:
                if (isConnected) SendPing();
                break;
            
            case TIMER_CONNECTION_CHECK:
                CheckConnectionHealth();
                break;
            
            case TIMER_BUFFER_CHECK:
                if (messageBuffer.Count > 0 && !isProcessingBuffer)
                {
                    ProcessMessageBuffer();
                }
                break;
            
            case TIMER_STATUS_LOG:
                LogConnectionStatus();
                break;
            
            case TIMER_QUALITY_CHECK:
                CheckConnectionQuality();
                break;
        }
    }

    #endregion

    #region ITwitchChatService Implementation
    
    // Implement SendChatMessage required by ITwitchChatService
    public void SendChatMessage(string message)
    {
        if (string.IsNullOrEmpty(message) || !isConnected || writer == null)
            return;
            
        try
        {
            writer.WriteLine($"PRIVMSG #{channelName} :{message}");
            Debug.Log($"[TwitchIRC] Sent message: {message}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TwitchIRC] Error sending message: {ex.Message}");
        }
    }
    
    #endregion

    // Consolidated health checking
    private void CheckConnectionHealth()
    {
        // Only check when connected
        if (connectionState != ConnectionState.Connected) return;
        
        bool needsReconnect = false;
        
        // Check for stale connection (no messages for 5 minutes)
        if (wasEverConnected && totalMessagesReceived > 0 && 
            Time.time - lastMessageReceivedTime > 300f)
        {
            LogWarning("Potential connection issue: No messages received for 5 minutes");
            needsReconnect = true;
        }
        
        // Check for ping timeouts
        if (isPingPending && Time.time - lastPingSentTime > PONG_TIMEOUT)
        {
            LogWarning("PING timeout - no response received. Connection might be stale.");
            isPingPending = false;
            needsReconnect = true;
        }
        
        // Reconnect if needed
        if (needsReconnect && shouldReconnect && !isReconnecting)
        {
            AttemptReconnect();
        }
    }
}