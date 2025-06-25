using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using System.Text;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/**
 * StreamElementsAPI - Handles integration with StreamElements for points management
 * 
 * This class provides an interface for awarding points to users through the StreamElements API.
 * It includes support for both JWT and OAuth authentication, rate limiting, and request queueing.
 * 
 * Usage:
 * - Call AwardPoints(username, amount) to award points to a single user
 * - Call AwardPointsBatch(awards) to award points to multiple users at once
 * - Use GetQueueStatus() to check if requests are being rate limited
 * 
 * The class automatically handles:
 * - Token authentication
 * - Queue processing with rate limiting
 * - Request retries with exponential backoff
 * - Cleanup of stale requests
 */
public class StreamElementsAPI : MonoBehaviour
{
    #region Class Types
    
    private class PendingRequest
    {
        public string Username;
        public int Amount;
        public Action<bool> Callback;
        public int RetryCount = 0;
        public float CreationTime;
        
        public PendingRequest(string username, int amount, Action<bool> callback = null)
        {
            Username = username;
            Amount = amount;
            Callback = callback;
            CreationTime = Time.time;
        }

        public bool IsStale(float staleTimeInSeconds) => (Time.time - CreationTime) > staleTimeInSeconds;
    }

    [Serializable]
    private class PointsResponse
    {
        public string username;
        public int points;
    }

    #endregion

    #region Constants & API Settings
    
    private const string API_BASE_URL = "https://api.streamelements.com/kappa/v2";
    
    #endregion
    
    #region Configuration Fields
    
    [Header("Authentication Method")]
    [SerializeField, Tooltip("Use JWT token instead of OAuth")]
    private bool useJwtToken = false;
    
    [Header("Rate Limiting")]
    [SerializeField, Tooltip("Maximum requests per minute to avoid API rate limits")]
    private int maxRequestsPerMinute = 60;
    [SerializeField, Tooltip("Error recovery delay when rate limited (seconds)")]
    private float rateLimitRecoveryDelay = 60f;
    
    [Header("Request Handling")]
    [SerializeField, Tooltip("Retry requests on network errors")]
    private bool retryFailedRequests = true;
    [SerializeField, Tooltip("Initial time to wait before retry (seconds)")]
    private float initialRetryDelay = 2f;
    [SerializeField, Tooltip("Maximum delay between retries (seconds)")]
    private float maxRetryDelay = 60f;
    [SerializeField, Tooltip("Factor to increase backoff time by after each retry")]
    private float retryBackoffFactor = 1.5f;
    [SerializeField, Tooltip("Maximum number of retry attempts")]
    private int maxRetryAttempts = 5;
    [SerializeField, Tooltip("Network request timeout in seconds")]
    private float requestTimeout = 10f;
    [SerializeField, Tooltip("Time after which a request is considered stale (minutes)")]
    private float requestStaleTime = 10f;
    [SerializeField, Tooltip("Interval between queue cleanups (seconds)")]
    private float queueCleanupInterval = 60f;
    
    #endregion
    
    #region State Fields
    
    // Authentication
    private string accessToken;
    private string cachedAuthToken;
    private float lastTokenFetchTime;
    private float tokenCacheDuration = 60f; // Cache tokens for 60 seconds
    
    // Rate limiting tracking
    private Queue<float> requestTimestamps = new Queue<float>();
    private bool isRateLimited = false;
    private float rateLimitEndTime = 0f;
    
    // Request queue
    private Queue<PendingRequest> pendingRequests = new Queue<PendingRequest>();
    private bool isProcessingQueue = false;
    private float lastQueueCleanupTime = 0f;
    
    #endregion
    
    #region Properties
    
    public bool IsSetup => !string.IsNullOrEmpty(GetChannelId()) && 
                           (!useJwtToken || !string.IsNullOrEmpty(accessToken));
    
    #endregion
    
    #region Unity Lifecycle Methods
    
    private void Start()
    {
        Initialize();
    }
    
    private void Update()
    {
        // Process the request queue if we have pending requests
        if (pendingRequests.Count > 0 && !isProcessingQueue)
        {
            StartCoroutine(ProcessRequestQueue());
        }
        
        // Clean up old timestamps outside the rate limit window
        CleanupOldRequestTimestamps();
        
        // Periodically clean up stale requests from the queue
        if (Time.time - lastQueueCleanupTime > queueCleanupInterval)
        {
            CleanupStaleRequests();
            lastQueueCleanupTime = Time.time;
        }
    }
    
    private void OnDisable()
    {
        // Gracefully handle any pending requests
        if (pendingRequests.Count > 0)
        {
            Debug.LogWarning($"[StreamElementsAPI] {pendingRequests.Count} pending point requests were cancelled on disable.");
            StopAllCoroutines();
        }
    }
    
    #endregion
    
    #region Initialization
    
    public void Initialize()
    {
        if (TokenManager.Instance == null)
        {
            Debug.LogError("StreamElementsAPI could not find a TokenManager in the scene!");
            return;
        }
        
        // Get OAuth access token if using OAuth
        if (!useJwtToken)
        {
            accessToken = TokenManager.Instance.GetStreamElementsAccessToken();
        }
        
        // Initialize cached token
        cachedAuthToken = null;
        lastTokenFetchTime = 0;
    }
    
    #endregion
    
    #region Public API Methods
    
    public bool AwardPoints(string username, int amount, Action<bool> callback = null)
    {
        if (!IsSetup)
        {
            Debug.LogError("[StreamElementsAPI] Not properly set up. Check TokenManager configuration.");
            InvokeCallback(callback, false);
            return false;
        }
        
        if (string.IsNullOrEmpty(username))
        {
            Debug.LogError("[StreamElementsAPI] Username cannot be empty");
            InvokeCallback(callback, false);
            return false;
        }
        
        // Add the request to the queue
        PendingRequest request = new PendingRequest(username, amount, callback);
        pendingRequests.Enqueue(request);
        
        return true;
    }
    
    public bool AwardPointsBatch(IEnumerable<(string username, int amount)> awards)
    {
        if (!IsSetup)
        {
            Debug.LogError("[StreamElementsAPI] Not properly set up. Check TokenManager configuration.");
            return false;
        }
        
        if (awards == null || !awards.Any())
        {
            Debug.LogWarning("[StreamElementsAPI] No awards to process");
            return false;
        }
        
        bool allValid = true;
        
        // Queue up all the awards
        foreach (var award in awards)
        {
            if (string.IsNullOrEmpty(award.username))
            {
                Debug.LogWarning("[StreamElementsAPI] Skipped award with empty username");
                allValid = false;
                continue;
            }
            
            PendingRequest request = new PendingRequest(award.username, award.amount);
            pendingRequests.Enqueue(request);
        }
        
        return allValid;
    }
    
    public void GetUserPoints(string username, Action<int, bool> callback)
    {
        if (!IsSetup)
        {
            Debug.LogError("[StreamElementsAPI] Not properly set up. Check TokenManager configuration.");
            if (callback != null)
            {
                callback.Invoke(0, false);
            }
            return;
        }
        
        if (string.IsNullOrEmpty(username))
        {
            Debug.LogError("[StreamElementsAPI] Username cannot be empty");
            if (callback != null)
            {
                callback.Invoke(0, false);
            }
            return;
        }
        
        StartCoroutine(GetUserPointsCoroutine(username, callback));
    }
    
    public void SetAuthenticationMethod(bool useJwt)
    {
        if (useJwt == useJwtToken) return;
        
        useJwtToken = useJwt;
        
        // Invalidate cached token since auth method changed
        cachedAuthToken = null;
        lastTokenFetchTime = 0;
        
        if (!useJwtToken && TokenManager.Instance != null)
        {
            // Fetch OAuth token
            accessToken = TokenManager.Instance.GetStreamElementsAccessToken();
        }
    }
    
    public (int pendingCount, bool isRateLimited, float rateLimitRemaining) GetQueueStatus()
    {
        float rateLimitRemaining = 0;
        
        if (isRateLimited)
        {
            rateLimitRemaining = Mathf.Max(0, rateLimitEndTime - Time.time);
        }
        else if (!CanMakeRequest())
        {
            rateLimitRemaining = GetTimeUntilNextRequestAllowed();
        }
        
        return (pendingRequests.Count, isRateLimited, rateLimitRemaining);
    }
    
    #endregion
    
    #region API Token Management
    
    private string GetChannelId()
    {
        return TokenManager.Instance != null ? TokenManager.Instance.GetStreamElementsChannelId() : string.Empty;
    }
    
    private string GetAuthToken()
    {
        // Check if we have a valid cached token
        if (cachedAuthToken != null && Time.time - lastTokenFetchTime < tokenCacheDuration)
        {
            return cachedAuthToken;
        }
        
        // No valid cache, fetch fresh token
        if (TokenManager.Instance == null)
        {
            Debug.LogError("No TokenManager reference!");
            return string.Empty;
        }
        
        string token;
        
        if (useJwtToken)
        {
            // Using JWT token
            token = TokenManager.Instance.GetStreamElementsJwtToken();
        }
        else
        {
            // Using OAuth token
            token = TokenManager.Instance.GetStreamElementsAccessToken();
            accessToken = token; // Update our cached copy
        }
        
        // Update cache
        cachedAuthToken = token;
        lastTokenFetchTime = Time.time;
        
        return token;
    }
    
    private void CheckAndRefreshToken()
    {
        if (useJwtToken) return; // JWT tokens don't need refresh in this context
        
        // Check if our cached token might be stale, and invalidate our cache
        // This will force the next GetAuthToken call to fetch a fresh token
        if (Time.time - lastTokenFetchTime > tokenCacheDuration / 2)
        {
            cachedAuthToken = null;
            lastTokenFetchTime = 0;
        }
    }
    
    private string GetPointsUrl(string username = null, int? amount = null)
    {
        string channelId = GetChannelId();
        
        if (string.IsNullOrEmpty(channelId))
        {
            return string.Empty;
        }
        
        string endpoint = $"{API_BASE_URL}/points/{channelId}";
        
        if (!string.IsNullOrEmpty(username))
        {
            endpoint += $"/{username}";
            
            if (amount.HasValue)
            {
                endpoint += $"/{amount.Value}";
            }
        }
        
        return endpoint;
    }
    
    #endregion
    
    #region Request Queue Management
    
    private void CleanupStaleRequests()
    {
        // Convert to seconds
        float staleTimeInSeconds = requestStaleTime * 60f;
        
        // If queue is empty, nothing to clean
        if (pendingRequests.Count == 0) return;
        
        // Create a temporary list to hold non-stale requests
        List<PendingRequest> nonStaleRequests = new List<PendingRequest>();
        int staleCount = 0;
        
        // Check each request
        while (pendingRequests.Count > 0)
        {
            PendingRequest request = pendingRequests.Dequeue();
            
            if (request.IsStale(staleTimeInSeconds))
            {
                // Handle stale request
                staleCount++;
                
                // Invoke callback with failure
                InvokeCallback(request.Callback, false);
            }
            else
            {
                // Keep non-stale request
                nonStaleRequests.Add(request);
            }
        }
        
        // If we removed any, log a warning
        if (staleCount > 0)
        {
            Debug.LogWarning($"[StreamElementsAPI] Removed {staleCount} stale requests from queue");
        }
        
        // Put the non-stale requests back in the queue
        foreach (PendingRequest request in nonStaleRequests)
        {
            pendingRequests.Enqueue(request);
        }
    }
    
    private IEnumerator ProcessRequestQueue()
    {
        isProcessingQueue = true;
        
        while (pendingRequests.Count > 0)
        {
            // Check if we're rate limited or can't make request now
            if (isRateLimited)
            {
                float remainingTime = Mathf.Max(0, rateLimitEndTime - Time.time);
                
                if (remainingTime > 0)
                {
                    yield return new WaitForSeconds(remainingTime);
                }
                
                    isRateLimited = false;
            }
            else if (!CanMakeRequest())
            {
                // Rate limiting - wait until we can make another request
                float timeToWait = GetTimeUntilNextRequestAllowed();
                yield return new WaitForSeconds(timeToWait);
            }
            
            // We've waited, now try to process the next request
            CheckAndRefreshToken();
            
            // Rate limit check passed, process the next request
            PendingRequest request = pendingRequests.Dequeue();
            
            // Track this request for rate limiting
                requestTimestamps.Enqueue(Time.time);
                
            // Start the coroutine to award points
            yield return StartCoroutine(AwardPointsCoroutine(
                request.Username,
                request.Amount,
                (success) => HandleRequestResult(request, success)
            ));
            
            // Small delay between requests to avoid hammering the API
            yield return new WaitForSeconds(0.1f);
        }
        
        isProcessingQueue = false;
    }
    
    private void HandleRequestResult(PendingRequest request, bool success)
    {
        if (success)
        {
            // Success! Invoke the callback
            InvokeCallback(request.Callback, true);
                    }
                    else
                    {
            // Failed, check if we should retry
            if (retryFailedRequests && request.RetryCount < maxRetryAttempts)
            {
                // Increment retry count and requeue with backoff
                request.RetryCount++;
                StartCoroutine(RetryAfterDelay(request));
            }
            else
            {
                // Too many retries or retry disabled, give up and inform caller
                InvokeCallback(request.Callback, false);
            }
        }
    }
    
    private IEnumerator RetryAfterDelay(PendingRequest request)
    {
        float delay = initialRetryDelay * Mathf.Pow(retryBackoffFactor, request.RetryCount - 1);
        delay = Mathf.Min(delay, maxRetryDelay);
        
        yield return new WaitForSeconds(delay);
        
        // Add back to the queue
        pendingRequests.Enqueue(request);
    }
    
    #endregion
    
    #region API Request Methods
    
    private IEnumerator AwardPointsCoroutine(string username, int amount, Action<bool> callback = null)
    {
        string authToken = GetAuthToken();
        
        if (string.IsNullOrEmpty(authToken))
        {
            InvokeCallback(callback, false);
            yield break;
        }
        
        using (UnityWebRequest request = CreatePointsWebRequest(username, amount, authToken))
        {
            request.timeout = Mathf.RoundToInt(requestTimeout);
            
            yield return request.SendWebRequest();
            
            ProcessWebRequestResult(request, amount, username, callback);
        }
    }
    
    private UnityWebRequest CreatePointsWebRequest(string username, int amount, string authToken)
    {
        string url = GetPointsUrl(username, amount);
        
        UnityWebRequest request = new UnityWebRequest(url, "PUT");
        request.SetRequestHeader("Authorization", useJwtToken ? $"Bearer {authToken}" : $"OAuth {authToken}");
        request.SetRequestHeader("Accept", "application/json");
        request.downloadHandler = new DownloadHandlerBuffer();
        
        return request;
    }
    
    private void ProcessWebRequestResult(UnityWebRequest request, int amount, string username, Action<bool> callback)
    {
        if (request.result == UnityWebRequest.Result.ConnectionError || 
            request.result == UnityWebRequest.Result.ProtocolError)
        {
            if (request.responseCode == 429) // Too Many Requests
            {
                isRateLimited = true;
                rateLimitEndTime = Time.time + rateLimitRecoveryDelay;
                Debug.LogWarning($"[StreamElementsAPI] Rate limited! Waiting {rateLimitRecoveryDelay} seconds before trying again.");
            }
            else
            {
                Debug.LogError($"[StreamElementsAPI] Error: {request.error} - Response: {request.downloadHandler.text}");
            }
            
            InvokeCallback(callback, false);
        }
        else
        {
            InvokeCallback(callback, true);
        }
    }
    
    private IEnumerator GetUserPointsCoroutine(string username, Action<int, bool> callback)
    {
        string authToken = GetAuthToken();
        
        if (string.IsNullOrEmpty(authToken))
        {
            callback?.Invoke(0, false);
            yield break;
        }
        
        string url = GetPointsUrl(username);
        
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.SetRequestHeader("Authorization", useJwtToken ? $"Bearer {authToken}" : $"OAuth {authToken}");
            request.SetRequestHeader("Accept", "application/json");
            request.timeout = Mathf.RoundToInt(requestTimeout);
            
            yield return request.SendWebRequest();
            
            if (request.result == UnityWebRequest.Result.ConnectionError || 
                request.result == UnityWebRequest.Result.ProtocolError)
            {
                if (request.responseCode == 429) // Too Many Requests
                {
                    isRateLimited = true;
                    rateLimitEndTime = Time.time + rateLimitRecoveryDelay;
                    Debug.LogWarning($"[StreamElementsAPI] Rate limited during points query! Waiting {rateLimitRecoveryDelay} seconds before trying again.");
                }
                else
                {
                    Debug.LogError($"[StreamElementsAPI] Error getting points: {request.error} - Response: {request.downloadHandler.text}");
                }
                
                callback?.Invoke(0, false);
            }
            else
            {
                try
                {
                    string jsonResponse = request.downloadHandler.text;
                    PointsResponse response = JsonUtility.FromJson<PointsResponse>(jsonResponse);
                    
                    if (response != null)
                    {
                        callback?.Invoke(response.points, true);
                    }
                    else
                    {
                        Debug.LogError($"[StreamElementsAPI] Failed to parse points response: {jsonResponse}");
                        callback?.Invoke(0, false);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[StreamElementsAPI] Error processing points response: {e.Message}");
                    callback?.Invoke(0, false);
                }
            }
        }
    }
    
    #endregion
    
    #region Rate Limiting
    
    private bool CanMakeRequest()
    {
        // If rate limited, check if we've passed the end time
        if (isRateLimited && Time.time >= rateLimitEndTime)
        {
            isRateLimited = false;
        }
        
        // If we're rate limited, we can't make a request
        if (isRateLimited) return false;
        
        // Check we haven't exceeded max requests per minute
        return requestTimestamps.Count < maxRequestsPerMinute;
    }
    
    private float GetTimeUntilNextRequestAllowed()
    {
        if (requestTimestamps.Count == 0) return 0;
        
        // If we're below the threshold, we can make a request now
        if (requestTimestamps.Count < maxRequestsPerMinute) return 0;
        
        // Calculate when the oldest request will fall outside the 1-minute window
        float oldestTimestamp = requestTimestamps.Peek();
        float timeSinceOldest = Time.time - oldestTimestamp;
        
        // If the oldest request is more than 60 seconds old, it will fall out
        // of the window in the next cleanup, so we can make a request now
        if (timeSinceOldest >= 60f) return 0;
        
        // Otherwise, we need to wait until the oldest request is outside the window
        return 60f - timeSinceOldest;
    }
    
    private void CleanupOldRequestTimestamps()
    {
        // Remove timestamps that are older than 1 minute
        float cutoffTime = Time.time - 60f;
        
        while (requestTimestamps.Count > 0 && requestTimestamps.Peek() < cutoffTime)
        {
            requestTimestamps.Dequeue();
        }
    }
    
    #endregion
    
    #region Utilities
    
    private void InvokeCallback(Action<bool> callback, bool success)
    {
        if (callback != null)
        {
            try
            {
                callback.Invoke(success);
            }
            catch (Exception e)
            {
                Debug.LogError($"[StreamElementsAPI] Error in callback: {e.Message}");
            }
        }
    }
    
    #endregion
}