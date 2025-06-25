using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// Manages authentication tokens for Twitch and StreamElements
/// Handles token refresh and persistence
public class TokenManager : MonoBehaviour
{
    // Singleton instance
    private static TokenManager _instance;
    
    // Public property to access the singleton instance
    public static TokenManager Instance 
    { 
        get 
        {
            if (_instance == null)
            {
                _instance = FindAnyObjectByType<TokenManager>();
                
                if (_instance == null)
                {
                    // Only log error in play mode
                    if (Application.isPlaying)
                    {
                        Debug.LogError("No TokenManager instance found in the scene!");
                    }
                }
            }
            return _instance;
        }
        private set { _instance = value; }
    }

    // Public property to check if TokenManager is properly initialized
    public bool IsInitialized => isInitialized && 
                                (!string.IsNullOrEmpty(twitchAccessToken) || 
                                 !string.IsNullOrEmpty(streamElementsAccessToken) || 
                                 !string.IsNullOrEmpty(streamElementsJwtToken));

    [Serializable]
    private class TokenData
    {
        // Twitch OAuth fields
        public string twitchClientId;
        public string twitchClientSecret;
        public string twitchAccessToken;
        public string twitchRefreshToken;
        public long twitchTokenExpiryTicks; // Store as ticks for serialization
        public string[] twitchScopes;
        
        // StreamElements OAuth fields
        public string streamElementsClientId;
        public string streamElementsClientSecret;
        public string streamElementsAccessToken;
        public string streamElementsRefreshToken;
        public long streamElementsTokenExpiryTicks; // Store as ticks for serialization
        public string streamElementsChannelId;
        public string[] streamElementsScopes;
        
        // JWT token (legacy auth)
        public string streamElementsJwtToken;
    }

    [Header("Twitch Authentication")]
    [SerializeField, Tooltip("Your Twitch Application Client ID")]
    private string twitchClientId;
    
    [SerializeField, Tooltip("Your Twitch Application Client Secret")]
    private string twitchClientSecret;

    [Header("Twitch Identity")]
    [SerializeField, Tooltip("The Twitch username this application will connect as")]
    private string twitchUsername;

    [SerializeField, Tooltip("The Twitch channel whose chat this application will join")]
    private string twitchChannel;

    [Header("Twitch Access Tokens")]
    [SerializeField, Tooltip("The current Twitch access token")]
    private string twitchAccessToken;

    [SerializeField, Tooltip("The Twitch refresh token used to get new access tokens")]
    private string twitchRefreshToken;

    [SerializeField, Tooltip("When the current Twitch token will expire")]
    private DateTime twitchTokenExpiry;

    [Header("Twitch Authorization")]
    [SerializeField, Tooltip("Authorization URL to navigate to in a browser")]
    private string authorizationUrl;
    
    [SerializeField, Tooltip("Authorization code received from redirect")]
    private string authorizationCode;
    
    [SerializeField, Tooltip("Generate authorization URL based on client ID")]
    private bool generateTwitchAuthUrl = false;
    
    [SerializeField, Tooltip("Exchange auth code for tokens")]
    private bool exchangeTwitchAuthCode = false;
    
    private string tokenStoragePath;
    private bool isInitialized = false;
    
    [Header("StreamElements Authentication")]
    [SerializeField, Tooltip("Your StreamElements Client ID")]
    private string streamElementsClientId;
    
    [SerializeField, Tooltip("Your StreamElements Client Secret")]
    private string streamElementsClientSecret;
    
    [SerializeField, Tooltip("Your StreamElements Channel ID")]
    private string streamElementsChannelId;
    
    [Header("StreamElements Access Tokens")]
    [SerializeField, Tooltip("The current StreamElements access token")]
    private string streamElementsAccessToken;
    
    [SerializeField, Tooltip("The StreamElements refresh token used to get new access tokens")]
    private string streamElementsRefreshToken;
    
    [SerializeField, Tooltip("When the current StreamElements token will expire")]
    private DateTime streamElementsTokenExpiry;
    
    // JWT token for StreamElements (legacy authentication method)
    [SerializeField, Tooltip("Your StreamElements JWT token (legacy auth)")]
    private string streamElementsJwtToken;
    
    [Header("StreamElements Authorization")]
    [SerializeField, Tooltip("Authorization URL for StreamElements")]
    private string streamElementsAuthUrl;
    
    [SerializeField, Tooltip("StreamElements authorization code received from redirect")]
    private string streamElementsAuthCode;
    
    [SerializeField, Tooltip("Generate StreamElements authorization URL")]
    private bool generateStreamElementsAuthUrl = false;
    
    [SerializeField, Tooltip("Exchange StreamElements auth code for tokens")]
    private bool exchangeStreamElementsAuthCode = false;
    
    [Header("Automatic Token Refresh")]
    [SerializeField] private float tokenRefreshSafetyMarginMinutes = 60f; // Refresh tokens 1 hour before expiry
    
    // Standard token lifetimes
    private float defaultTwitchTokenLifetimeHours = 4f;
    private float defaultStreamElementsTokenLifetimeHours = 24 * 30f; // 30 days
    
    // Cache formatted oauth token to avoid string allocations
    private string cachedTwitchOAuthToken;
    private bool twitchOAuthTokenDirty = true;
    
    // Notification strings
    private readonly string twitchRefreshWarning = "⚠️ IMPORTANT: Twitch token needs manual refresh! Please update in the Inspector. ⚠️";
    private readonly string streamElementsRefreshWarning = "⚠️ IMPORTANT: StreamElements token needs manual refresh! Please update in the Inspector. ⚠️";
    
    private bool isRefreshingTwitch = false;
    private bool isRefreshingStreamElements = false;
    
    // Retry settings
    private const int MaxRetries = 3;
    private const float InitialRetryDelay = 2f;
    
    // Add this to display token status in the inspector
    [Header("Token Status")]
    [SerializeField] private bool checkTokenStatus = false;
    
    [Header("Debug Options")]
    [SerializeField] private bool verboseLogging = false;
    
    private bool hasTwitchTokenErrors = false;
    private bool hasStreamElementsTokenErrors = false;
    
    void Awake()
    {
        InitializeSingleton();
    }
    
    void OnEnable()
    {
        // Ensure initialization happens if object was disabled
        if (_instance == this && !isInitialized)
        {
            LoadTokens();
            StartTokenChecks();
        }
    }
    
    private void InitializeSingleton()
    {
        // Singleton pattern implementation
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Multiple TokenManager instances detected");
            Destroy(gameObject);
            return;
        }
        
        _instance = this;
        
        // Ensure the GameObject is at root level before calling DontDestroyOnLoad
        if (transform.parent != null)
        {
            transform.SetParent(null);
        }
        DontDestroyOnLoad(gameObject);
        
        // Ensure we have a valid path for token storage
        if (string.IsNullOrEmpty(Application.persistentDataPath))
        {
            Debug.LogError("Application.persistentDataPath is empty! Using fallback path.");
            tokenStoragePath = Path.Combine(Application.dataPath, "tokens.json");
        }
        else
        {
            tokenStoragePath = Path.Combine(Application.persistentDataPath, "tokens.json");
        }
        
        // Only log path when verbose logging is enabled
        if (verboseLogging) Debug.Log($"Token storage path: {tokenStoragePath}");
        
        LoadTokens();
        
        // Initialize token data with defaults if invalid
        InitializeTokenData();
        
        isInitialized = !string.IsNullOrEmpty(twitchAccessToken) && 
                        !string.IsNullOrEmpty(twitchRefreshToken) &&
                        twitchTokenExpiry > DateTime.UtcNow.AddMinutes(10);
                        
        StartTokenChecks();
    }
    
    private void StartTokenChecks()
    {
        // Start token refresh check
        StartCoroutine(CheckTokenRefreshRoutine());
    }

    private void Start()
    {
        // Start token refresh check - redundant, moved to InitializeSingleton
    }
    
    private void OnDestroy()
    {
        // Ensure we save any pending changes
        if (_instance == this)
        {
            SaveTokens();
            StopAllCoroutines();
        }
    }
    
    private void OnApplicationQuit()
    {
        // Save tokens when application quits
        if (_instance == this)
        {
            SaveTokens();
        }
    }
    
    void OnValidate()
    {
        // Generate the authorization URL when requested for Twitch
        if (generateTwitchAuthUrl && !string.IsNullOrEmpty(twitchClientId))
        {
            generateTwitchAuthUrl = false;
            // ABSOLUTELY ALL Twitch scopes from the comprehensive documentation
            string scopes = "analytics:read:extensions+analytics:read:games+bits:read+channel:bot+channel:manage:ads+channel:read:ads+channel:manage:broadcast+channel:read:charity+channel:edit:commercial+channel:read:editors+channel:manage:extensions+channel:read:goals+channel:read:guest_star+channel:manage:guest_star+channel:read:hype_train+channel:manage:moderators+channel:read:polls+channel:manage:polls+channel:read:predictions+channel:manage:predictions+channel:manage:raids+channel:read:redemptions+channel:manage:redemptions+channel:manage:schedule+channel:read:stream_key+channel:read:subscriptions+channel:manage:videos+channel:read:vips+channel:manage:vips+channel:moderate+clips:edit+moderation:read+moderator:manage:announcements+moderator:manage:automod+moderator:read:automod_settings+moderator:manage:automod_settings+moderator:read:banned_users+moderator:manage:banned_users+moderator:read:blocked_terms+moderator:manage:blocked_terms+moderator:read:chat_messages+moderator:manage:chat_messages+moderator:read:chat_settings+moderator:manage:chat_settings+moderator:read:chatters+moderator:read:followers+moderator:read:guest_star+moderator:manage:guest_star+moderator:read:moderators+moderator:read:shield_mode+moderator:manage:shield_mode+moderator:read:shoutouts+moderator:manage:shoutouts+moderator:read:suspicious_users+moderator:read:unban_requests+moderator:manage:unban_requests+moderator:read:vips+moderator:read:warnings+moderator:manage:warnings+user:bot+user:edit+user:edit:broadcast+user:read:blocked_users+user:manage:blocked_users+user:read:broadcast+user:read:chat+user:manage:chat_color+user:read:email+user:read:emotes+user:read:follows+user:read:moderated_channels+user:read:subscriptions+user:read:whispers+user:manage:whispers+user:write:chat+chat:edit+chat:read+whispers:read";
            authorizationUrl = $"https://id.twitch.tv/oauth2/authorize?response_type=code&client_id={twitchClientId}&redirect_uri=https://zamburg1.github.io/twitch-auth-redirect/&scope={scopes}&state={Guid.NewGuid()}";
            Debug.Log($"Authorization URL generated. Open this in a browser: {authorizationUrl}");
        }
        
        // Exchange the authorization code for tokens when requested for Twitch
        if (exchangeTwitchAuthCode && !string.IsNullOrEmpty(authorizationCode))
        {
            exchangeTwitchAuthCode = false;
            StartCoroutine(ExchangeCodeForTokens());
        }
        
        // Generate the authorization URL when requested for StreamElements
        if (generateStreamElementsAuthUrl && !string.IsNullOrEmpty(streamElementsClientId))
        {
            generateStreamElementsAuthUrl = false;
            // All confirmed valid StreamElements scopes
            string scopes = "channel:read+tips:read+tips:write+activities:read+activities:write+loyalty:read+loyalty:write+overlays:read+overlays:write+bot:read+bot:write";
            streamElementsAuthUrl = $"https://api.streamelements.com/oauth2/authorize?response_type=code&client_id={streamElementsClientId}&redirect_uri=https://zamburg1.github.io/SE-assets/&scope={scopes}&state={Guid.NewGuid()}";
            Debug.Log($"StreamElements Authorization URL generated. Open this in a browser: {streamElementsAuthUrl}");
        }
        
        // Exchange the authorization code for tokens when requested for StreamElements
        if (exchangeStreamElementsAuthCode && !string.IsNullOrEmpty(streamElementsAuthCode))
        {
            exchangeStreamElementsAuthCode = false;
            StartCoroutine(ExchangeStreamElementsCodeForTokens());
        }
        
        // Check token status when requested
        if (checkTokenStatus)
        {
            checkTokenStatus = false;
            DisplayTokenStatus();
        }
    }
    
    private IEnumerator ExchangeCodeForTokens()
    {
        string url = "https://id.twitch.tv/oauth2/token";
        WWWForm form = new WWWForm();
        form.AddField("client_id", twitchClientId);
        form.AddField("client_secret", twitchClientSecret);
        form.AddField("code", authorizationCode);
        form.AddField("grant_type", "authorization_code");
        form.AddField("redirect_uri", "https://zamburg1.github.io/twitch-auth-redirect/");

        yield return MakeTokenRequest<TokenResponse>(
            url,
            form,
            (response) => {
                twitchAccessToken = response.access_token;
                twitchRefreshToken = response.refresh_token;
                twitchTokenExpiry = DateTime.UtcNow.AddSeconds(response.expires_in);
                twitchOAuthTokenDirty = true;
                
                // Store the scopes
                if (response.scope != null && response.scope.Length > 0)
                {
                    Debug.Log($"<color=green>Twitch token granted with {response.scope.Length} scopes</color>");
                }
                else if (!string.IsNullOrEmpty(response.scope_string))
                {
                    string[] scopes = response.scope_string.Split(' ');
                    response.scope = scopes;
                    Debug.Log($"<color=green>Twitch token granted with {scopes.Length} scopes from string</color>");
                }
                
                SaveTokens();
                isInitialized = true;
                
                Debug.Log("<color=green>Successfully obtained Twitch tokens! Token will expire on: " + twitchTokenExpiry.ToString() + "</color>");
                
                // Clear the authorization code for security
                authorizationCode = "";
            },
            null
        );
    }
    
    private IEnumerator ExchangeStreamElementsCodeForTokens()
    {
        string url = "https://api.streamelements.com/oauth2/token";
        WWWForm form = new WWWForm();
        form.AddField("client_id", streamElementsClientId);
        form.AddField("client_secret", streamElementsClientSecret);
        form.AddField("code", streamElementsAuthCode);
        form.AddField("grant_type", "authorization_code");
        form.AddField("redirect_uri", "https://zamburg1.github.io/SE-assets/");

        yield return MakeTokenRequest<StreamElementsTokenResponse>(
            url,
            form,
            (response) => {
                streamElementsAccessToken = response.access_token;
                streamElementsRefreshToken = response.refresh_token;
                streamElementsTokenExpiry = DateTime.UtcNow.AddSeconds(response.expires_in);
                
                // Store scopes if available
                if (!string.IsNullOrEmpty(response.scope))
                {
                    string[] scopes = response.scope.Split(' ');
                    Debug.Log($"<color=green>StreamElements token granted with {scopes.Length} scopes</color>");
                }
                
                SaveTokens();
                
                Debug.Log("<color=green>Successfully obtained StreamElements tokens! Token will expire on: " + streamElementsTokenExpiry.ToString() + "</color>");
                
                // Clear the authorization code for security
                streamElementsAuthCode = "";
            },
            null
        );
    }
    
    [Serializable]
    private class TokenResponse
    {
        public string access_token;
        public string refresh_token;
        public int expires_in;
        public string[] scope;
        public string scope_string;
        public string token_type;
    }
    
    [Serializable]
    private class StreamElementsTokenResponse
    {
        public string access_token;
        public string refresh_token;
        public int expires_in;
        public string token_type;
        public string scope;
    }
    
    private DateTime GetDefaultTwitchExpiry()
    {
        // Set to 75% of the typical Twitch token lifetime
        return DateTime.UtcNow.AddHours(defaultTwitchTokenLifetimeHours * 0.75f);
    }
    
    private DateTime GetDefaultStreamElementsExpiry()
    {
        // Set to 75% of the typical StreamElements token lifetime
        return DateTime.UtcNow.AddHours(defaultStreamElementsTokenLifetimeHours * 0.75f);
    }
    
    private void LoadTokens()
    {
        if (!File.Exists(tokenStoragePath)) 
        {
            #if UNITY_EDITOR
            // Only log in editor mode if verbose logging is enabled
            if (verboseLogging && Application.isPlaying)
            {
                Debug.Log($"No tokens file found at: {tokenStoragePath}");
            }
            #else
            if (verboseLogging)
            {
                Debug.Log($"No tokens file found at: {tokenStoragePath}");
            }
            #endif
            return;
        }
        
        try
        {
            string json = File.ReadAllText(tokenStoragePath);
            TokenData data = JsonUtility.FromJson<TokenData>(json);
            
            twitchClientId = data.twitchClientId;
            twitchClientSecret = data.twitchClientSecret;
            twitchAccessToken = data.twitchAccessToken;
            twitchRefreshToken = data.twitchRefreshToken;
            
            // Convert ticks back to DateTime
            try 
            {
                if (data.twitchTokenExpiryTicks > 0)
                {
                    twitchTokenExpiry = new DateTime(data.twitchTokenExpiryTicks);
                    if (verboseLogging) Debug.Log($"Loaded Twitch token expiry: {twitchTokenExpiry}");
                }
                else
                {
                    hasTwitchTokenErrors = true;
                    Debug.LogWarning("Invalid Twitch token expiry ticks found in saved data. Setting to a realistic future time.");
                    twitchTokenExpiry = GetDefaultTwitchExpiry();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error parsing Twitch token expiry: {e.Message}");
                hasTwitchTokenErrors = true;
                twitchTokenExpiry = GetDefaultTwitchExpiry();
            }
            
            twitchOAuthTokenDirty = true;
            
            streamElementsClientId = data.streamElementsClientId;
            streamElementsClientSecret = data.streamElementsClientSecret;
            streamElementsAccessToken = data.streamElementsAccessToken;
            streamElementsRefreshToken = data.streamElementsRefreshToken;
            
            // Convert ticks back to DateTime
            try 
            {
                if (data.streamElementsTokenExpiryTicks > 0)
                {
                    streamElementsTokenExpiry = new DateTime(data.streamElementsTokenExpiryTicks);
                    if (verboseLogging) Debug.Log($"Loaded StreamElements token expiry: {streamElementsTokenExpiry}");
                }
                else
                {
                    hasStreamElementsTokenErrors = true;
                    Debug.LogWarning("Invalid StreamElements token expiry ticks found in saved data. Setting to a realistic future time.");
                    streamElementsTokenExpiry = GetDefaultStreamElementsExpiry();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error parsing StreamElements token expiry: {e.Message}");
                hasStreamElementsTokenErrors = true;
                streamElementsTokenExpiry = GetDefaultStreamElementsExpiry();
            }
            
            streamElementsChannelId = data.streamElementsChannelId;
            streamElementsJwtToken = data.streamElementsJwtToken;
            
            if (verboseLogging) Debug.Log("Tokens loaded successfully");
        }
        catch (Exception e)
        {
            Debug.LogError($"Error loading tokens: {e.Message}");
        }
    }
    
    private bool IsValidDateTime(DateTime dateTime)
    {
        return dateTime.Year >= 2000 && dateTime.Year <= 3000;
    }
    
    private void SaveTokens()
    {
        try
        {
            // Check if the path is valid
            if (string.IsNullOrEmpty(tokenStoragePath))
            {
                Debug.LogError("Cannot save tokens: Token storage path is empty");
                
                // Try to set a valid path if it's empty
                if (!string.IsNullOrEmpty(Application.persistentDataPath))
                {
                    tokenStoragePath = Path.Combine(Application.persistentDataPath, "tokens.json");
                    if (verboseLogging) Debug.Log($"Reset token storage path to: {tokenStoragePath}");
                }
                else if (!string.IsNullOrEmpty(Application.dataPath))
                {
                    tokenStoragePath = Path.Combine(Application.dataPath, "tokens.json");
                    if (verboseLogging) Debug.Log($"Using fallback token storage path: {tokenStoragePath}");
                }
                else
                {
                    Debug.LogError("Failed to find a valid path to save tokens");
                    return;
                }
            }
            
            // Ensure directory exists
            string directory = Path.GetDirectoryName(tokenStoragePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            TokenData data = new TokenData
            {
                twitchClientId = twitchClientId,
                twitchClientSecret = twitchClientSecret,
                twitchAccessToken = twitchAccessToken,
                twitchRefreshToken = twitchRefreshToken,
                twitchTokenExpiryTicks = twitchTokenExpiry.Ticks,
                streamElementsClientId = streamElementsClientId,
                streamElementsClientSecret = streamElementsClientSecret,
                streamElementsAccessToken = streamElementsAccessToken,
                streamElementsRefreshToken = streamElementsRefreshToken,
                streamElementsTokenExpiryTicks = streamElementsTokenExpiry.Ticks,
                streamElementsChannelId = streamElementsChannelId,
                streamElementsJwtToken = streamElementsJwtToken
            };
            
            string json = JsonUtility.ToJson(data);
            File.WriteAllText(tokenStoragePath, json);
            if (verboseLogging)
            {
                Debug.Log($"<color=green>Tokens successfully saved to: {tokenStoragePath}</color>");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error saving tokens: {e.Message}");
            Debug.LogException(e);
        }
    }
    
    // Methods to access tokens from other components
    public string GetTwitchAccessToken()
    {
        if (string.IsNullOrEmpty(twitchAccessToken)) return string.Empty;
        
        try
        {
            // Check if token expiry is a valid date before comparing
            if (!IsValidDateTime(twitchTokenExpiry))
            {
                hasTwitchTokenErrors = true;
                if (verboseLogging) Debug.LogWarning("Twitch token expiry date is invalid. Initializing to a realistic future value.");
                twitchTokenExpiry = GetDefaultTwitchExpiry();
                return twitchAccessToken;
            }
            
            if (DateTime.UtcNow > twitchTokenExpiry.AddMinutes(-10))
            {
                RefreshTwitchToken();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error in GetTwitchAccessToken: {e.Message}");
            twitchTokenExpiry = GetDefaultTwitchExpiry();
        }
        
        return twitchAccessToken;
    }
    
    public string GetTwitchClientId()
    {
        return twitchClientId;
    }
    
    public string GetStreamElementsClientId()
    {
        return streamElementsClientId;
    }
    
    public string GetStreamElementsChannelId()
    {
        return streamElementsChannelId;
    }
    
    public string GetStreamElementsAccessToken()
    {
        if (string.IsNullOrEmpty(streamElementsAccessToken)) return string.Empty;
        
        try
        {
            // Check if token expiry is a valid date before comparing
            if (!IsValidDateTime(streamElementsTokenExpiry))
            {
                hasStreamElementsTokenErrors = true;
                if (verboseLogging) Debug.LogWarning("StreamElements token expiry date is invalid. Initializing to a realistic future value.");
                streamElementsTokenExpiry = GetDefaultStreamElementsExpiry();
                return streamElementsAccessToken;
            }
            
            if (DateTime.UtcNow > streamElementsTokenExpiry.AddMinutes(-10))
            {
                RefreshStreamElementsToken();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error in GetStreamElementsAccessToken: {e.Message}");
            streamElementsTokenExpiry = GetDefaultStreamElementsExpiry();
        }
        
        return streamElementsAccessToken;
    }
    
    // Format for environment variables
    public string GetTwitchOAuthFormatted()
    {
        if (!twitchOAuthTokenDirty && !string.IsNullOrEmpty(cachedTwitchOAuthToken))
        {
            return cachedTwitchOAuthToken;
        }
        
        string token = GetTwitchAccessToken();
        
        if (string.IsNullOrEmpty(token)) return string.Empty;
        
        if (!token.StartsWith("oauth:"))
        {
            token = "oauth:" + token;
        }
        
        cachedTwitchOAuthToken = token;
        twitchOAuthTokenDirty = false;
        
        return token;
    }

    public string GetTwitchUsername()
    {
        // Use the inspector value directly
        return twitchUsername;
    }

    public string GetTwitchChannel()
    {
        // Use inspector value, fall back to username if not specified
        return string.IsNullOrEmpty(twitchChannel) ? twitchUsername : twitchChannel;
    }

    private IEnumerator CheckTokenRefreshRoutine()
    {
        while (true)
        {
            // Check tokens every 15 minutes
            yield return new WaitForSeconds(15 * 60);
            
            CheckAndRefreshTokens();
        }
    }
    
    public void CheckAndRefreshTokens()
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan safetyMargin = TimeSpan.FromMinutes(tokenRefreshSafetyMarginMinutes);
        
        try
        {
            // Check Twitch token
            if (!IsValidDateTime(twitchTokenExpiry))
            {
                hasTwitchTokenErrors = true;
                if (verboseLogging) Debug.LogWarning("Twitch token has invalid expiry date. Setting to a realistic future time.");
                twitchTokenExpiry = GetDefaultTwitchExpiry();
            }
            else if (now + safetyMargin >= twitchTokenExpiry)
            {
                if (verboseLogging) Debug.LogWarning($"Twitch token approaching expiry (expires {twitchTokenExpiry}). Attempting refresh.");
                RefreshTwitchToken();
            }
            
            // Check StreamElements token
            if (!IsValidDateTime(streamElementsTokenExpiry))
            {
                hasStreamElementsTokenErrors = true;
                if (verboseLogging) Debug.LogWarning("StreamElements token has invalid expiry date. Setting to a realistic future time.");
                streamElementsTokenExpiry = GetDefaultStreamElementsExpiry();
            }
            else if (now + safetyMargin >= streamElementsTokenExpiry)
            {
                if (verboseLogging) Debug.LogWarning($"StreamElements token approaching expiry (expires {streamElementsTokenExpiry}). Attempting refresh.");
                RefreshStreamElementsToken();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error in CheckAndRefreshTokens: {e.Message}");
            // Reset expiry dates to realistic future times
            twitchTokenExpiry = GetDefaultTwitchExpiry();
            streamElementsTokenExpiry = GetDefaultStreamElementsExpiry();
        }
    }
    
    private void RefreshTwitchToken()
    {
        // Avoid multiple refresh attempts
        if (isRefreshingTwitch) return;
        
        // Try automatic refresh if we have refresh token
        if (!string.IsNullOrEmpty(twitchRefreshToken))
        {
            isRefreshingTwitch = true;
            StartCoroutine(RefreshTwitchTokenAutomatically());
        }
        else
        {
            // Fall back to manual notification if we don't have a refresh token
            Debug.LogWarning(twitchRefreshWarning);
            StartCoroutine(ShowTokenRefreshNotification(twitchRefreshWarning));
        }
    }
    
    private void RefreshStreamElementsToken()
    {
        // Avoid multiple refresh attempts
        if (isRefreshingStreamElements) return;
        
        // Try automatic refresh first if we have refresh token
        if (!string.IsNullOrEmpty(streamElementsRefreshToken))
        {
            isRefreshingStreamElements = true;
            StartCoroutine(RefreshStreamElementsTokenAutomatically());
        }
        else
        {
            // Fall back to manual notification if we don't have a refresh token
            Debug.LogWarning(streamElementsRefreshWarning);
            StartCoroutine(ShowTokenRefreshNotification(streamElementsRefreshWarning));
        }
    }
    
    /// Generic method to handle token-related API requests with retry logic
    private IEnumerator MakeTokenRequest<T>(string url, WWWForm form, Action<T> onSuccess, Action onFailure) 
        where T : class
    {
        int retryAttempt = 0;
        bool success = false;
        
        while (!success && retryAttempt <= MaxRetries)
        {
            if (retryAttempt > 0)
            {
                // Apply exponential backoff
                float delayTime = InitialRetryDelay * Mathf.Pow(2, retryAttempt - 1);
                yield return new WaitForSeconds(delayTime);
            }
            
            retryAttempt++;

            using (UnityWebRequest request = UnityWebRequest.Post(url, form))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (retryAttempt > MaxRetries)
                    {
                        Debug.LogError($"Request failed after {MaxRetries} attempts: {request.error}");
                        onFailure?.Invoke();
                        yield break;
                    }
                    continue;
                }

                string response = request.downloadHandler.text;
                
                try
                {
                    T responseObj = JsonUtility.FromJson<T>(response);
                    
                    if (responseObj != null)
                    {
                        onSuccess(responseObj);
                        success = true;
                    }
                    else
                    {
                        throw new Exception("Invalid response format");
                    }
                }
                catch (Exception ex)
                {
                    if (retryAttempt > MaxRetries)
                    {
                        Debug.LogError($"Error parsing response: {ex.Message}");
                        onFailure?.Invoke();
                        yield break;
                    }
                }
            }
        }
    }
    
    private IEnumerator RefreshTwitchTokenAutomatically()
    {
        string url = "https://id.twitch.tv/oauth2/token";
        WWWForm form = new WWWForm();
        form.AddField("client_id", twitchClientId);
        form.AddField("client_secret", twitchClientSecret);
        form.AddField("refresh_token", twitchRefreshToken);
        form.AddField("grant_type", "refresh_token");

        yield return MakeTokenRequest<TokenResponse>(
            url,
            form,
            (response) => {
                // Successfully refreshed
                twitchAccessToken = response.access_token;
                twitchOAuthTokenDirty = true;
                
                // Update refresh token if provided
                if (!string.IsNullOrEmpty(response.refresh_token))
                {
                    twitchRefreshToken = response.refresh_token;
                }
                
                // Update expiry
                twitchTokenExpiry = DateTime.UtcNow.AddSeconds(response.expires_in);
                
                // Save tokens
                SaveTokens();
                
                if (verboseLogging)
                {
                    Debug.Log("Successfully refreshed Twitch token automatically");
                }
                else
                {
                    Debug.Log("Token Manager: Authentication refreshed successfully");
                }
            },
            null
        );
        
        isRefreshingTwitch = false;
    }
    
    private IEnumerator RefreshStreamElementsTokenAutomatically()
    {
        string url = "https://api.streamelements.com/oauth2/token";
        WWWForm form = new WWWForm();
        form.AddField("client_id", streamElementsClientId);
        form.AddField("client_secret", streamElementsClientSecret);
        form.AddField("refresh_token", streamElementsRefreshToken);
        form.AddField("grant_type", "refresh_token");
        form.AddField("redirect_uri", "https://zamburg1.github.io/SE-assets/");

        yield return MakeTokenRequest<StreamElementsTokenResponse>(
            url,
            form,
            (response) => {
                // Successfully refreshed
                streamElementsAccessToken = response.access_token;
                
                // Update refresh token if provided
                if (!string.IsNullOrEmpty(response.refresh_token))
                {
                    streamElementsRefreshToken = response.refresh_token;
                }
                
                // Update expiry
                streamElementsTokenExpiry = DateTime.UtcNow.AddSeconds(response.expires_in);
                
                // Save tokens
                SaveTokens();
                
                if (verboseLogging)
                {
                    Debug.Log("Successfully refreshed StreamElements token automatically");
                }
                else
                {
                    Debug.Log("Token Manager: Authentication refreshed successfully");
                }
            },
            null
        );
        
        isRefreshingStreamElements = false;
    }
    
    private IEnumerator ShowTokenRefreshNotification(string warningMessage)
    {
        // Display notification less frequently (3 warnings instead of 10)
        for (int i = 0; i < 3; i++)
        {
            Debug.LogWarning(warningMessage);
            
            // Wait 30 minutes before showing again
            yield return new WaitForSeconds(30 * 60);
        }
    }
    
    public void SetTwitchOAuth(string newToken, float lifetimeHours = 0)
    {
        if (string.IsNullOrEmpty(newToken)) return;
        
        // Update token
        twitchAccessToken = newToken;
        twitchOAuthTokenDirty = true;
        
        // Update expiry time
        float tokenLifetime = lifetimeHours > 0 ? lifetimeHours : defaultTwitchTokenLifetimeHours;
        twitchTokenExpiry = DateTime.UtcNow.AddHours(tokenLifetime);
        
        // Save tokens
        SaveTokens();
        
        Debug.Log($"Updated Twitch OAuth token. Expires: {twitchTokenExpiry}");
    }
    
    public void SetStreamElementsOAuth(string newAccessToken, string newRefreshToken, float lifetimeHours = 0)
    {
        if (string.IsNullOrEmpty(newAccessToken) || string.IsNullOrEmpty(newRefreshToken)) return;
        
        // Update tokens
        streamElementsAccessToken = newAccessToken;
        streamElementsRefreshToken = newRefreshToken;
        
        // Update expiry time
        float tokenLifetime = lifetimeHours > 0 ? lifetimeHours : defaultStreamElementsTokenLifetimeHours;
        streamElementsTokenExpiry = DateTime.UtcNow.AddHours(tokenLifetime);
        
        // Save tokens
        SaveTokens();
        
        Debug.Log($"Updated StreamElements OAuth token. Expires: {streamElementsTokenExpiry}");
    }

    public string GetStreamElementsJwtToken()
    {
        return streamElementsJwtToken;
    }
    
    public void SetStreamElementsJwtToken(string newToken)
    {
        if (string.IsNullOrEmpty(newToken)) return;
        
        streamElementsJwtToken = newToken;
        SaveTokens();
        
        Debug.Log("StreamElements JWT token updated");
    }

    public void DisplayTokenStatus()
    {
        // First ensure tokens are loaded when in editor mode
        #if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            LoadTokens();
            InitializeTokenData();
        }
        #endif

        StringBuilder status = new StringBuilder();
        status.AppendLine("==== TOKEN STATUS ====");
        
        // Twitch token status
        status.AppendLine("TWITCH:");
        if (!string.IsNullOrEmpty(twitchAccessToken))
        {
            string expiryStatus = DateTime.UtcNow < twitchTokenExpiry ? "Valid" : "EXPIRED";
            status.AppendLine($"  Token: {MaskToken(twitchAccessToken)} ({expiryStatus})");
            status.AppendLine($"  Expires: {twitchTokenExpiry.ToString()} UTC");
            status.AppendLine($"  Time remaining: {(twitchTokenExpiry - DateTime.UtcNow).TotalHours:F1} hours");
            
            // Display username and channel
            if (!string.IsNullOrEmpty(twitchUsername))
                status.AppendLine($"  Username: {twitchUsername}");
            if (!string.IsNullOrEmpty(twitchChannel)) 
                status.AppendLine($"  Channel: {twitchChannel}");
            
            // Display scopes by making a validation request to Twitch API
            StartCoroutine(GetAndDisplayTwitchScopes(twitchAccessToken));
        }
        else
        {
            status.AppendLine("  No token available");
        }
        
        // StreamElements token status
        status.AppendLine("STREAMELEMENTS:");
        if (!string.IsNullOrEmpty(streamElementsAccessToken))
        {
            string expiryStatus = DateTime.UtcNow < streamElementsTokenExpiry ? "Valid" : "EXPIRED";
            status.AppendLine($"  Token: {MaskToken(streamElementsAccessToken)} ({expiryStatus})");
            status.AppendLine($"  Expires: {streamElementsTokenExpiry.ToString()} UTC");
            status.AppendLine($"  Time remaining: {(streamElementsTokenExpiry - DateTime.UtcNow).TotalHours:F1} hours");
            
            // Display channel ID if available
            if (!string.IsNullOrEmpty(streamElementsChannelId))
                status.AppendLine($"  Channel ID: {streamElementsChannelId}");
            
            // Display scopes by making a validation request to StreamElements API
            StartCoroutine(GetAndDisplayStreamElementsScopes(streamElementsAccessToken));
        }
        else
        {
            status.AppendLine("  No token available");
        }
        
        if (!string.IsNullOrEmpty(streamElementsJwtToken))
        {
            status.AppendLine("  JWT Token: " + MaskToken(streamElementsJwtToken));
        }
        
        Debug.Log(status.ToString());
    }
    
    private IEnumerator GetAndDisplayTwitchScopes(string accessToken)
    {
        using (UnityWebRequest request = UnityWebRequest.Get("https://id.twitch.tv/oauth2/validate"))
        {
            request.SetRequestHeader("Authorization", "OAuth " + accessToken);
            
            yield return request.SendWebRequest();
            
            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string response = request.downloadHandler.text;
                    ValidationResponse validation = JsonUtility.FromJson<ValidationResponse>(response);
                    
                    // Add this line for consistency with StreamElements
                    Debug.Log("  TWITCH TOKEN: Valid");
                    
                    if (validation != null && validation.scopes != null && validation.scopes.Length > 0)
                    {
                        StringBuilder scopeList = new StringBuilder();
                        scopeList.AppendLine("  TWITCH SCOPES:");
                        
                        foreach (string scope in validation.scopes)
                        {
                            scopeList.AppendLine($"    • {scope}");
                        }
                        
                        Debug.Log(scopeList.ToString());
                    }
                    else
                    {
                        Debug.Log("  TWITCH SCOPES: No scopes found in validation response");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error parsing Twitch validation response: {ex.Message}");
                }
            }
            else
            {
                Debug.LogError($"Error validating Twitch token: {request.error}");
            }
        }
    }
    
    private IEnumerator GetAndDisplayStreamElementsScopes(string accessToken)
    {
        // Use the proper validation endpoint
        using (UnityWebRequest request = UnityWebRequest.Get("https://api.streamelements.com/oauth2/validate"))
        {
            request.SetRequestHeader("Authorization", "OAuth " + accessToken);
            
            yield return request.SendWebRequest();
            
            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string response = request.downloadHandler.text;
                    
                    // Parse the JSON response
                    ValidationResponseSE validation = JsonUtility.FromJson<ValidationResponseSE>(response);
                    
                    if (validation != null)
                    {
                        Debug.Log("  STREAMELEMENTS TOKEN: Valid");
                        
                        // Format scopes in the same style as Twitch
                        if (validation.scopes != null && validation.scopes.Length > 0)
                        {
                            StringBuilder scopeList = new StringBuilder();
                            scopeList.AppendLine("  STREAMELEMENTS SCOPES:");
                            
                            foreach (string scope in validation.scopes)
                            {
                                scopeList.AppendLine($"    • {scope}");
                            }
                            
                            Debug.Log(scopeList.ToString());
                        }
                        else if (!string.IsNullOrEmpty(response) && response.Contains("scope"))
                        {
                            // Try to extract scope from raw response if we couldn't parse it properly
                            Debug.Log("  STREAMELEMENTS SCOPES: Found in response but unable to parse format");
                            Debug.Log($"  Raw response: {response}");
                        }
                        else
                        {
                            Debug.Log("  STREAMELEMENTS SCOPES: No scopes found in token");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Error parsing StreamElements validation: {ex.Message}");
                    Debug.Log("  STREAMELEMENTS TOKEN: Valid (but couldn't parse scopes)");
                }
            }
            else
            {
                Debug.LogWarning($"StreamElements token validation error: {request.error}");
            }
        }
    }
    
    [Serializable]
    private class ValidationResponse
    {
        public string client_id;
        public string login;
        public string[] scopes;
        public int user_id;
        public int expires_in;
    }
    
    [Serializable]
    private class ValidationResponseSE
    {
        public string channel_id;
        public string client_id;
        public int expires_in;
        public string[] scopes;
        public string scope; // Some responses use a single string instead of array
    }
    
    private string MaskToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return "Empty";
        if (token.Length <= 10) return "****" + token.Substring(token.Length - 4);
        
        return token.Substring(0, 5) + "..." + token.Substring(token.Length - 5);
    }

    // Initialize tokens if not properly loaded
    private void InitializeTokenData()
    {
        bool inEditorNotPlaying = false;
        #if UNITY_EDITOR
        inEditorNotPlaying = !Application.isPlaying;
        #endif

        if (twitchTokenExpiry.Year < 2000 || twitchTokenExpiry.Year > 3000)
        {
            hasTwitchTokenErrors = true;
            if (verboseLogging && !inEditorNotPlaying) 
                Debug.LogWarning("Setting realistic Twitch token expiry during initialization");
            twitchTokenExpiry = GetDefaultTwitchExpiry();
        }
        
        if (streamElementsTokenExpiry.Year < 2000 || streamElementsTokenExpiry.Year > 3000)
        {
            hasStreamElementsTokenErrors = true;
            if (verboseLogging && !inEditorNotPlaying) 
                Debug.LogWarning("Setting realistic StreamElements token expiry during initialization");
            streamElementsTokenExpiry = GetDefaultStreamElementsExpiry();
        }
        
        // Show a single summary message about token status
        LogTokenSummary(inEditorNotPlaying);
    }
    
    private void LogTokenSummary(bool inEditorNotPlaying = false)
    {
        if (hasTwitchTokenErrors || hasStreamElementsTokenErrors)
        {
            if (!inEditorNotPlaying)
                Debug.Log("Token Manager: Set realistic token expiry times");
        }
        else
        {
            // Only use fancy color in the log if we have verboseLogging
            if (verboseLogging && !inEditorNotPlaying)
                Debug.Log("<color=green>Token Manager: All tokens loaded successfully and are valid.</color>");
            else if (!inEditorNotPlaying && verboseLogging)
                Debug.Log("Token Manager: Initialized successfully");
        }
    }
} 