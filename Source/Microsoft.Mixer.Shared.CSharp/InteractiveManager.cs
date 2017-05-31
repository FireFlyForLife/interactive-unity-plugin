﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading;
#if WINDOWS_UWP
using Windows.System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Security.Authentication.Web.Core;
using Windows.Security.Credentials;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Web.Http;
using System.Net.Http.Headers;
using Windows.Data.Json;
#else
using System.Security.Cryptography.X509Certificates;
using System.Timers;
using WebSocketSharp;
#endif

namespace Microsoft.Mixer
{
    /// <summary>
    /// Manager service class that handles connection with the Interactive
    /// service and your game.
    /// </summary>
    public partial class InteractivityManager : IDisposable
    {
        // Events
        public delegate void OnErrorEventHandler(object sender, InteractiveEventArgs e);
        public event OnErrorEventHandler OnError;

        // Just one state changed event. OnInteractivityStateChanged. Get rid of the other events.
        public delegate void OnInteractivityStateChangedHandler(object sender, InteractivityStateChangedEventArgs e);
        public event OnInteractivityStateChangedHandler OnInteractivityStateChanged;

        public delegate void OnParticipantStateChangedHandler(object sender, InteractiveParticipantStateChangedEventArgs e);
        public event OnParticipantStateChangedHandler OnParticipantStateChanged;

        public delegate void OnInteractiveButtonEventHandler(object sender, InteractiveButtonEventArgs e);
        public event OnInteractiveButtonEventHandler OnInteractiveButtonEvent;

        public delegate void OnInteractiveJoystickControlEventHandler(object sender, InteractiveJoystickEventArgs e);
        public event OnInteractiveJoystickControlEventHandler OnInteractiveJoystickControlEvent;

        private static InteractivityManager _singletonInstance;

        /// <summary>
        /// Gets the singleton instance of InteractivityManager.
        /// </summary>
        public static InteractivityManager SingletonInstance
        {
            get
            {
                if (_singletonInstance == null)
                {
                    _singletonInstance = new InteractivityManager();
                    _singletonInstance.InitializeInternal();
                }
                return _singletonInstance;
            }
        }

        /// <summary>
        /// Controls the amount of diagnostic output written by the Interactive SDK.
        /// </summary>
        public LoggingLevel LoggingLevel
        {
            get;
            set;
        }

        private string ProjectVersionID
        {
            get;
            set;
        }

        private string AppID
        {
            get;
            set;
        }

        /// <summary>
        /// Can query the state of the InteractivityManager.
        /// </summary>
        public InteractivityState InteractivityState
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets all the groups associated with the current interactivity instance.
        /// Will be empty if initialization is not complete.
        /// </summary>
        public IList<InteractiveGroup> Groups
        {
            get
            {
                return new List<InteractiveGroup>(_groups);
            }
        }

        /// <summary>
        /// Gets all the scenes associated with the current interactivity instance.
        /// Will be empty if initialization is not complete.
        /// </summary>
        public IList<InteractiveScene> Scenes
        {
            get
            {
                return new List<InteractiveScene>(_scenes);
            }
        }

        /// <summary>
        /// Returns all the participants.
        /// </summary>
        public IList<InteractiveParticipant> Participants
        {
            get
            {
                return new List<InteractiveParticipant>(_participants);
            }
        }

        private IList<InteractiveControl> Controls
        {
            get
            {
                return new List<InteractiveControl>(_controls);
            }
        }

        /// <summary>
        /// Retrieve a list of all of the button controls.
        /// </summary>
        public IList<InteractiveButtonControl> Buttons
        {
            get
            {
                return new List<InteractiveButtonControl>(_buttons);
            }
        }

        /// <summary>
        /// Retrieve a list of all of the joystick controls.
        /// </summary>
        public IList<InteractiveJoystickControl> Joysticks
        {
            get
            {
                return new List<InteractiveJoystickControl>(_joysticks);
            }
        }

        /// <summary>
        /// The string the broadcaster needs to enter in the Mixer website to
        /// authorize the interactive session.
        /// </summary>
        public string ShortCode
        {
            get;
            private set;
        }

        /// <summary>
        /// Returns the specified group. Will return null if initialization
        /// is not yet complete or group does not exist.
        /// </summary>
        /// <param name="groupID">The ID of the group.</param>
        /// <returns></returns>
        public InteractiveGroup GetGroup(string groupID)
        {
            foreach (InteractiveGroup group in _groups)
            {
                if (group.GroupID == groupID)
                {
                    return group;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns the specified scene. Will return nullptr if initialization
        /// is not yet complete or scene does not exist.
        /// </summary>
        public InteractiveScene GetScene(string sceneID)
        {
            var scenes = Scenes;
            foreach (InteractiveScene scene in scenes)
            {
                if (scene.SceneID == sceneID)
                {
                    return scene;
                }
            }
            return null;
        }

        /// <summary>
        /// Kicks off a background task to set up the connection to the interactivity service.
        /// </summary>
        /// <param name="goInteractive"> If true, initializes and enters interactivity. Defaults to true</param>
        ///// <param name="authToken">
        ///// A token to use for authentication. This is used for when a user is on a device
        ///// that supports Xbox Live tokens.
        ///// </param>
        /// <remarks></remarks>
        public void Initialize(bool goInteractive = true/*, string authToken = ""*/) // TODO: Sync with Molly on AuthToken
        {
            if (InteractivityState != InteractivityState.NotInitialized)
            {
                return;
            }

            ResetInternalState();
            UpdateInteractivityState(InteractivityState.Initializing);

            if (goInteractive)
            {
                _shouldStartInteractive = true;
            }
            //_authToken = authToken;
            InitiateConnection();
        }

        private void CreateStorageDirectoryIfNotExists()
        {
            if (!Directory.Exists(_streamingAssetsPath))
            {
                Directory.CreateDirectory(_streamingAssetsPath);
            }
        }

#if !WINDOWS_UWP
        private void InitiateConnection()
#else
        private async void InitiateConnection()
#endif
        {
            try
            {
                Uri getWebSocketUri = new Uri(WEBSOCKET_DISCOVERY_URL);
                string responseString = string.Empty;
#if !WINDOWS_UWP
                HttpWebRequest getWebSocketUrlRequest = new HttpWebRequest(getWebSocketUri);
                WebResponse response = getWebSocketUrlRequest.GetResponse();
                using (Stream stream = response.GetResponseStream())
                using (StreamReader streamReader = new StreamReader(response.GetResponseStream()))
                {
                    responseString = streamReader.ReadToEnd();
                    streamReader.Close();
                }
                response.Close();
#else
                var result = await _httpClient.GetAsync(getWebSocketUri);
                responseString = result.Content.ToString();
#endif
                using (StringReader stringReader = new StringReader(responseString))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
                {
                    while (jsonReader.Read())
                    {
                        if (jsonReader.Value != null &&
                            jsonReader.Value.ToString() == WS_MESSAGE_KEY_WEBSOCKET_ADDRESS)
                        {
                            jsonReader.Read();
                            _interactiveWebSocketUrl = jsonReader.Value.ToString();
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError(ex.Message);
                LogError("Error: Could not retrieve the URL for the websocket.");
            }

            if (AppID == null ||
                ProjectVersionID == null)
            {
                PopulateConfigData();
            }

            if (!string.IsNullOrEmpty(_authToken) ||
                TryGetAuthTokensFromCache())
            {
                ConnectToWebsocket();
            }
            else
            {
                    // Show a shortCode
#if !WINDOWS_UWP
                RefreshShortCode();

                _checkAuthStatusTimer.Elapsed += CheckAuthStatusCallback;
                _checkAuthStatusTimer.AutoReset = true;
                _checkAuthStatusTimer.Enabled = true;
                _checkAuthStatusTimer.Start();
#else
                await RefreshShortCode();
                _checkAuthStatusTimer = ThreadPoolTimer.CreatePeriodicTimer(
                    CheckAuthStatusCallback,
                    TimeSpan.FromMilliseconds(POLL_FOR_SHORT_CODE_AUTH_INTERVAL));
#endif
            }
        }

        private void PopulateConfigData()
        {
            string fullPathToConfigFile = string.Empty;
            if (_isUnity)
            {
                fullPathToConfigFile = _streamingAssetsPath + "/" + INTERACTIVE_CONFIG_FILE_NAME;
            }
            if (File.Exists(fullPathToConfigFile))
            {
                string configText = File.ReadAllText(fullPathToConfigFile);
                try
                {
                    using (StringReader stringReader = new StringReader(configText))
                    using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
                    {
                        while (jsonReader.Read())
                        {
                            if (jsonReader.Value != null)
                            {
                                string key = jsonReader.Value.ToString();
                                string lowercaseKey = key.ToLowerInvariant();
                                switch (lowercaseKey)
                                {
                                    case WS_MESSAGE_KEY_APPID:
                                        jsonReader.Read();
                                        if (jsonReader.Value != null)
                                        {
                                            AppID = jsonReader.Value.ToString();
                                        }
                                        break;
                                    case WS_MESSAGE_KEY_PROJECT_VERSION_ID:
                                        jsonReader.Read();
                                        if (jsonReader.Value != null)
                                        {
                                            ProjectVersionID = jsonReader.Value.ToString();
                                        }
                                        break;
                                    default:
                                        // No-op. We don't throw an error because the SDK only implements a
                                        // subset of the total possible server messages so we expect to see
                                        // method messages that we don't know how to handle.
                                        break;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    LogError("Error: interactiveconfig.json file could not be read. Make sure it is valid JSON and has the correct format.");
                }
            }
            else
            {
                throw new Exception("Error: You need to specify an AppID and ProjectVersionID in the Interactive Editor. You can get to the Interactivity Editor from the Mixer menu (Mixer > Open editor).");
            }
        }

#if !WINDOWS_UWP
        private void CheckAuthStatusCallback(object sender, ElapsedEventArgs e)
        {
            if (TryGetTokenAsync())
            {
                _refreshShortCodeTimer.Stop();
                _checkAuthStatusTimer.Stop();
                ConnectToWebsocket();
            }
        }
#else
        private async void CheckAuthStatusCallback(ThreadPoolTimer timer)
        {
            bool gotTokenResult = false;
            try
            {
                gotTokenResult = await TryGetTokenAsync();
            }
            catch (Exception ex)
            {
                if (ex.HResult == -2145844844)
                {
                    LogError("Erorr: Interactive project not found. Make sure you have the correct OAuth Client ID and Project Version ID.");
                }
            }
            if (gotTokenResult)
            {
                if (_refreshShortCodeTimer != null)
                {
                    _refreshShortCodeTimer.Cancel();
                }
                if (_checkAuthStatusTimer != null)
                {
                    _checkAuthStatusTimer.Cancel();
                }
                ConnectToWebsocket();
            }
        }
#endif

#if !WINDOWS_UWP
        private bool TryGetTokenAsync()
#else
        private async Task<bool> TryGetTokenAsync()
#endif
        {
            bool isAuthenticated = false;

#if !WINDOWS_UWP
            WebRequest getShortCodeStatusRequest = WebRequest.Create(API_CHECK_SHORT_CODE_AUTH_STATUS_PATH + _authShortCodeRequestHandle);
            getShortCodeStatusRequest.ContentType = "application/json";
            getShortCodeStatusRequest.Method = "GET";

            HttpWebResponse getShortCodeStatusResponse = (HttpWebResponse)getShortCodeStatusRequest.GetResponse();
            switch (getShortCodeStatusResponse.StatusCode)
            {
                case System.Net.HttpStatusCode.OK:
                    using (Stream getShortCodeStatusDataStream = getShortCodeStatusResponse.GetResponseStream())
                    using (StreamReader getShortCodeStatusReader = new StreamReader(getShortCodeStatusDataStream))
                    {
                        string getShortCodeStatusServerResponse = getShortCodeStatusReader.ReadToEnd();
                        string oauthExchangeCode = ParseOAuthExchangeCodeFromStringResponse(getShortCodeStatusServerResponse);

                        _checkAuthStatusTimer.Stop();
                        GetOauthToken(oauthExchangeCode);

                        isAuthenticated = true;
                    }
                    break;
                case System.Net.HttpStatusCode.NoContent:
                    // No-op: still waiting for user input.
                    break;
                default:
                    // No-op
                    break;
            }
#else
            try
            {
                Uri checkShortCodeUrl = new Uri(API_CHECK_SHORT_CODE_AUTH_STATUS_PATH + _authShortCodeRequestHandle);
                HttpResponseMessage response = await _httpClient.GetAsync(checkShortCodeUrl);
                if (response.StatusCode == Windows.Web.Http.HttpStatusCode.Ok)
                {
                    string getShortCodeStatusServerResponse = response.Content.ToString();
                    string oauthExchangeCode = ParseOAuthExchangeCodeFromStringResponse(getShortCodeStatusServerResponse);
                    if (_checkAuthStatusTimer != null)
                    {
                        _checkAuthStatusTimer.Cancel();
                    }
                    GetOauthToken(oauthExchangeCode);
                    isAuthenticated = true;
                }
                else if (response.StatusCode != Windows.Web.Http.HttpStatusCode.NoContent)
                {
                    LogError("Error: Error while trying to check the short code response. Status code: " + response.StatusCode);
                }
            }
            catch (WebException we)
            {
                var webResponse = we.Response as HttpWebResponse;
                if (webResponse.StatusCode != System.Net.HttpStatusCode.NoContent)
                {
                    LogError("Error: Error while trying to check the short code response. Status code: " + webResponse.StatusCode.ToString());
                }
                else
                {
                    LogError("Error: Error while trying to check the short code response.");
                }
            }
#endif
            return isAuthenticated;
        }

        private string ParseOAuthExchangeCodeFromStringResponse(string responseText)
        {
            string oauthExchangeCode = string.Empty;
            using (StringReader stringReader = new StringReader(responseText))
            using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
            {
                while (jsonReader.Read() && oauthExchangeCode == string.Empty)
                {
                    if (jsonReader.Value != null &&
                        jsonReader.Value.ToString() == WS_MESSAGE_KEY_CODE)
                    {
                        jsonReader.Read();
                        oauthExchangeCode = jsonReader.Value.ToString();
                    }
                }
            }
            return oauthExchangeCode;
        }

#if !WINDOWS_UWP
        private void GetOauthToken(string exchangeCode)
#else
        private async void GetOauthToken(string exchangeCode)
#endif
        {
            string getCodeServerResponse = string.Empty;
            try
            {
#if !WINDOWS_UWP
                WebRequest getCodeRequest = WebRequest.Create(API_GET_OAUTH_TOKEN_PATH);
                getCodeRequest.ContentType = "application/json";
                getCodeRequest.Method = "POST";

                ASCIIEncoding encoding = new ASCIIEncoding();
                string stringData = "{ \"client_id\": \"" + AppID + "\", \"code\": \"" + exchangeCode + "\", \"grant_type\": \"authorization_code\" }";
                byte[] data = encoding.GetBytes(stringData);

                getCodeRequest.ContentLength = data.Length;

                Stream newStream = getCodeRequest.GetRequestStream();
                newStream.Write(data, 0, data.Length);
                newStream.Close();

                // Get the response.
                HttpWebResponse getCodeResponse = (HttpWebResponse)getCodeRequest.GetResponse();
                using (Stream getCodeDataStream = getCodeResponse.GetResponseStream())
                using (StreamReader getCodeReader = new StreamReader(getCodeDataStream))
                {
                    getCodeServerResponse = getCodeReader.ReadToEnd();
                }
                getCodeResponse.Close();
#else
                Uri authTokenUri = new Uri(API_GET_OAUTH_TOKEN_PATH);
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, authTokenUri);
                request.Content = new HttpStringContent("{ \"client_id\": \"" + AppID + "\", \"code\": \"" + exchangeCode + "\", \"grant_type\": \"authorization_code\" }",
                    Windows.Storage.Streams.UnicodeEncoding.Utf8,
                    "application/json");
                try
                {
                    var result = await _httpClient.SendRequestAsync(request);
                    getCodeServerResponse = result.Content.ToString();
                }
                catch (Exception ex)
                {
                    LogError("Error: Error trying to get the short code. Error code: " + ex.HResult);
                }
#endif
                string refreshToken = string.Empty;
                string accessToken = string.Empty;
                using (StringReader stringReader = new StringReader(getCodeServerResponse))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
                {
                    while (jsonReader.Read() && (accessToken == string.Empty || refreshToken == string.Empty))
                    {
                        if (jsonReader.Value != null)
                        {
                            if (jsonReader.Value.ToString() == WS_MESSAGE_KEY_WEBSOCKET_ACCESS_TOKEN)
                            {
                                jsonReader.Read();
                                accessToken = jsonReader.Value.ToString();
                            }
                            else if (jsonReader.Value.ToString() == WS_MESSAGE_KEY_REFRESH_TOKEN)
                            {
                                jsonReader.Read();
                                refreshToken = jsonReader.Value.ToString();
                            }
                        }
                    }
                }
                _authToken = "Bearer " + accessToken;
                _oauthRefreshToken = refreshToken;

                WriteAuthTokensToCache();
            }
            catch
            {
                LogError("Error: Could not request an OAuth Token.");
            }
        }

#if !WINDOWS_UWP
        private void RefreshShortCode()
#else
        private async Task RefreshShortCode()
#endif
        {
            int shortCodeExpirationTime = -1;
            string getShortCodeServerResponse = string.Empty;
            try
            {
#if !WINDOWS_UWP
                HttpWebRequest getShortCodeRequest = (HttpWebRequest)WebRequest.Create(API_GET_SHORT_CODE_PATH);
                getShortCodeRequest.ContentType = "application/json";
                getShortCodeRequest.Method = "POST";
                ASCIIEncoding encoding = new ASCIIEncoding();
                string stringData = "{ \"client_id\": \"" + AppID + "\", \"scope\": \"interactive:robot:self\" }";
                byte[] data = encoding.GetBytes(stringData);

                getShortCodeRequest.ContentLength = data.Length;
                Stream newStream = getShortCodeRequest.GetRequestStream();
                newStream.Write(data, 0, data.Length);
                newStream.Close();

                HttpWebResponse getShortCodeResponse = (HttpWebResponse)getShortCodeRequest.GetResponse();
                using (Stream getShortCodeDataStream = getShortCodeResponse.GetResponseStream())
                using (StreamReader getShortCodeReader = new StreamReader(getShortCodeDataStream))
                {
                    getShortCodeServerResponse = getShortCodeReader.ReadToEnd();
                }
                getShortCodeResponse.Close();
#else
                Uri authTokenUri = new Uri(API_GET_SHORT_CODE_PATH);
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, authTokenUri);
                request.Content = new HttpStringContent("{ \"client_id\": \"" + AppID + "\", \"scope\": \"interactive:robot:self\" }",
                    Windows.Storage.Streams.UnicodeEncoding.Utf8,
                    "application/json");
                try
                {
                    var result = await _httpClient.SendRequestAsync(request);
                    getShortCodeServerResponse = result.Content.ToString();
                }
                catch (Exception ex)
                {
                    LogError("Error: Error trying to get the short code. Error code: " + ex.HResult);
                }
#endif
                using (StringReader stringReader = new StringReader(getShortCodeServerResponse))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
                {
                    while (jsonReader.Read())
                    {
                        if (jsonReader.Value != null)
                        {
                            string key = jsonReader.Value.ToString();
                            string lowercaseKey = key.ToLowerInvariant();
                            switch (lowercaseKey)
                            {
                                case WS_MESSAGE_KEY_CODE:
                                    jsonReader.Read();
                                    if (jsonReader.Value != null)
                                    {
                                        ShortCode = jsonReader.Value.ToString();
                                    }
                                    break;
                                case WS_MESSAGE_KEY_EXPIRATION:
                                    jsonReader.Read();
                                    if (jsonReader.Value != null)
                                    {
                                        shortCodeExpirationTime = Convert.ToInt32(jsonReader.Value.ToString());
                                    }
                                    break;
                                case WS_MESSAGE_KEY_HANDLE:
                                    jsonReader.Read();
                                    if (jsonReader.Value != null)
                                    {
                                        _authShortCodeRequestHandle = jsonReader.Value.ToString();
                                    }
                                    break;
                                default:
                                    // No-op. We don't throw an error because the SDK only implements a
                                    // subset of the total possible server messages so we expect to see
                                    // method messages that we don't know how to handle.
                                    break;
                            }
                        }
                    }
                }
#if !WINDOWS_UWP
                _refreshShortCodeTimer.Interval = shortCodeExpirationTime * SECONDS_IN_A_MILLISECOND;
                _refreshShortCodeTimer.Enabled = true;
                _refreshShortCodeTimer.Start();
#else
                _refreshShortCodeTimer = ThreadPoolTimer.CreatePeriodicTimer(
                    RefreshShortCodeCallback,
                    TimeSpan.FromMilliseconds(shortCodeExpirationTime * SECONDS_IN_A_MILLISECOND));
#endif

                UpdateInteractivityState(InteractivityState.ShortCodeRequired);
            }
            catch
            {
                LogError("Error: Failed to get a new short code for authentication.");
            }
        }

#if !WINDOWS_UWP
        private bool VerifyAuthToken()
#else
        private async Task<bool> VerifyAuthToken()
#endif
        {
            bool isTokenValid = false;
            try
            {
                // Make an HTTP request against the WebSocket and if it returns a non-401 response
                // then the token is still valid.
#if !WINDOWS_UWP
                WebRequest testWebSocketAuthRequest = WebRequest.Create(_interactiveWebSocketUrl.Replace("wss", "https"));
                testWebSocketAuthRequest.Method = "GET";
                WebHeaderCollection headerCollection1 = new WebHeaderCollection();
                headerCollection1.Add("Authorization", _authToken);
                headerCollection1.Add("X-Interactive-Version", ProjectVersionID);
                headerCollection1.Add("X-Protocol-Version", PROTOCOL_VERSION);
                testWebSocketAuthRequest.Headers = headerCollection1;

                HttpWebResponse testWebSocketAuthResponse = (HttpWebResponse)testWebSocketAuthRequest.GetResponse();
                if (testWebSocketAuthResponse.StatusCode == HttpStatusCode.OK)
                {
                    isTokenValid = true;
                }
                testWebSocketAuthResponse.Close();
#else
                var response = await _httpClient.GetAsync(new Uri(_interactiveWebSocketUrl.Replace("wss", "https")));
                if (response.StatusCode == Windows.Web.Http.HttpStatusCode.Unauthorized)
                {
                    isTokenValid = false;
                }
                else if (response.StatusCode == Windows.Web.Http.HttpStatusCode.BadRequest)
                {
                    // 400 - Bad request will happen when upgrading the web socket an
                    // means the request succeeded.
                    isTokenValid = true;
                }
                else
                {
                    LogError("Error: Failed to while trying to validate a cached auth token.");
                }
#endif
            }
            catch (WebException we)
            {
                var response = we.Response as HttpWebResponse;
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    isTokenValid = false;
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    // 400 - Bad request will happen when upgrading the web socket an
                    // means the request succeeded.
                    isTokenValid = true;
                }
                else
                {
                    LogError("Error: Failed to while trying to validate a cached auth token.");
                }
            }
            return isTokenValid;
        }

#if !WINDOWS_UWP
        private void RefreshAuthToken()
#else
        private async void RefreshAuthToken()
#endif
        {
            string getCodeServerResponse = string.Empty;
            try
            {
#if !WINDOWS_UWP
                WebRequest getCodeRequest = WebRequest.Create(API_GET_OAUTH_TOKEN_PATH);
                getCodeRequest.ContentType = "application/json";
                getCodeRequest.Method = "POST";

                ASCIIEncoding encoding = new ASCIIEncoding();
                string stringData = "{ \"client_id\": \"" + AppID + "\", \"refresh_token\": \"" + _oauthRefreshToken + "\", \"grant_type\": \"refresh_token\" }";
                byte[] data = encoding.GetBytes(stringData);

                getCodeRequest.ContentLength = data.Length;

                Stream newStream = getCodeRequest.GetRequestStream();
                newStream.Write(data, 0, data.Length);
                newStream.Close();

                WebResponse getCodeResponse = getCodeRequest.GetResponse();
                using (Stream getCodeDataStream = getCodeResponse.GetResponseStream())
                using (StreamReader getCodeReader = new StreamReader(getCodeDataStream))
                {
                    getCodeServerResponse = getCodeReader.ReadToEnd();
                }
                getCodeResponse.Close();
#else
                Uri authTokenUri = new Uri(API_GET_OAUTH_TOKEN_PATH);
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, authTokenUri);
                request.Content = new HttpStringContent("{ \"client_id\": \"" + AppID + "\", \"refresh_token\": \"" + _oauthRefreshToken + "\", \"grant_type\": \"refresh_token\" }",
                    Windows.Storage.Streams.UnicodeEncoding.Utf8,
                    "application/json");
                try
                {
                    var result = await _httpClient.SendRequestAsync(request);
                    getCodeServerResponse = result.Content.ToString();
                }
                catch (Exception ex)
                {
                    LogError("Error: Error trying to get the short code. Error code: " + ex.HResult);
                }
#endif

                string accessToken = string.Empty;
                string refreshToken = string.Empty;

                using (StringReader stringReader = new StringReader(getCodeServerResponse))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
                {
                    while (jsonReader.Read() && accessToken == string.Empty && refreshToken == string.Empty)
                    {
                        if (jsonReader.Value != null)
                        {
                            if (jsonReader.Value.ToString() == WS_MESSAGE_KEY_WEBSOCKET_ACCESS_TOKEN)
                            {
                                jsonReader.Read();
                                accessToken = jsonReader.Value.ToString();
                            }
                            else if (jsonReader.Value.ToString() == WS_MESSAGE_KEY_REFRESH_TOKEN)
                            {
                                jsonReader.Read();
                                refreshToken = jsonReader.Value.ToString();
                            }
                        }
                    }
                }
                _authToken = "Bearer " + accessToken;
                _oauthRefreshToken = refreshToken;
                WriteAuthTokensToCache();
            }
            catch
            {
                LogError("Error: Unable to refresh the auth token.");
            }
        }

#if !WINDOWS_UWP
        private void ConnectToWebsocket()
#else
        private async void ConnectToWebsocket()
#endif
        {
            if (_pendingConnectToWebSocket)
            {
                return;
            }
            _pendingConnectToWebSocket = true;

            bool isTokenValid = false;
#if !WINDOWS_UWP
            isTokenValid = VerifyAuthToken();
#else
            isTokenValid = await VerifyAuthToken();
#endif
            if (!isTokenValid)
            {
                RefreshAuthToken();
            }

#if !WINDOWS_UWP
            _websocket = new WebSocket(_interactiveWebSocketUrl);

            NameValueCollection headerCollection = new NameValueCollection();
            headerCollection.Add("Authorization", _authToken);
            headerCollection.Add("X-Interactive-Version", ProjectVersionID);
            headerCollection.Add("X-Protocol-Version", PROTOCOL_VERSION);
            _websocket.SetHeaders(headerCollection);

            // Start a timer in case we never see the open event. WebSocketSharp
            // doesn't properly expose connection errors.

            _websocket.OnOpen += OnWebsocketOpen;
            _websocket.OnMessage += OnWebSocketMessage;
            _websocket.OnError += OnWebSocketError;
            _websocket.OnClose += OnWebSocketClose;
            _websocket.Connect();
#else
            try
            {
                _websocket.SetRequestHeader("Authorization", _authToken);
                _websocket.SetRequestHeader("X-Interactive-Version", ProjectVersionID);
                _websocket.SetRequestHeader("X-Protocol-Version", PROTOCOL_VERSION);

                _websocket.MessageReceived += OnWebSocketMessage;
                _websocket.Closed += OnWebSocketClose;
                await _websocket.ConnectAsync(new Uri(_interactiveWebSocketUrl));
                if (_reconnectTimer != null)
                {
                    _reconnectTimer.Cancel();
                }
                SendGetAllGroupsMessage();
                SendGetAllScenesMessage();
            }
            catch (Exception ex)
            {
                string foo = ex.Message;
                var bar = ex.HResult;
                LogError("asdsad");
            }
#endif
        }

#if !WINDOWS_UWP
        private void OnWebSocketClose(object sender, CloseEventArgs e)
        {
            UpdateInteractivityState(InteractivityState.InteractivityDisabled);
            // Any type of error means we didn't succeed in connecting. If that happens we need to try to reconnect.
            // We do a retry with a reduced interval.
            _pendingConnectToWebSocket = false;
            _reconnectTimer.Start();
        }
#else
            private void OnWebSocketClose(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            UpdateInteractivityState(InteractivityState.InteractivityDisabled);
            // Any type of error means we didn't succeed in connecting. If that happens we need to try to reconnect.
            // We do a retry with a reduced interval.
            _pendingConnectToWebSocket = false;
            _reconnectTimer = ThreadPoolTimer.CreatePeriodicTimer(
                    CheckAuthStatusCallback,
                    TimeSpan.FromMilliseconds(WEBSOCKET_RECONNECT_INTERVAL));
        }
#endif

        private bool TryGetAuthTokensFromCache()
        {
            bool succeeded = false;
#if !WINDOWS_UWP
            string fullPathToDataFile = string.Empty;
            if (_isUnity)
            {
                fullPathToDataFile = _streamingAssetsPath + "/" + INTERACTIVE_DATA_FILE_NAME;
            }
            if (File.Exists(fullPathToDataFile))
            {
                try
                {
                    string interactiveDataText = File.ReadAllText(fullPathToDataFile);
                    string authToken = string.Empty;
                    string refreshToken = string.Empty;

                    using (StringReader stringReader = new StringReader(interactiveDataText))
                    using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
                    {
                        while (jsonReader.Read() && (authToken == string.Empty || refreshToken == string.Empty))
                        {
                            if (jsonReader.Value != null)
                            {
                                if (jsonReader.Value.ToString() == WS_MESSAGE_KEY_ACCESS_TOKEN_FROM_FILE)
                                {
                                    jsonReader.Read();
                                    authToken = jsonReader.Value.ToString();
                                }
                                else if (jsonReader.Value.ToString() == WS_MESSAGE_KEY_REFRESH_TOKEN_FROM_FILE)
                                {
                                    jsonReader.Read();
                                    refreshToken = jsonReader.Value.ToString();
                                }
                            }
                        }
                    }

                    _authToken = authToken;
                    _oauthRefreshToken = refreshToken;
                    succeeded = true;
                }
                catch
                {
                    LogError("Error: Error reading the cached interactive data file.");
                }
            }
            else
            {
                Log("No cached token found. This is an expected case.");
            }
#else
            var localSettings = ApplicationData.Current.LocalSettings;
            if (localSettings.Values["Mixer-AuthToken"] != null)
            {
                _authToken = localSettings.Values["Mixer-AuthToken"].ToString();
            }
            if (localSettings.Values["Mixer-RefreshToken"] != null)
            {
                _oauthRefreshToken = localSettings.Values["Mixer-RefreshToken"].ToString();
            }
#endif
            return succeeded;
        }

        private void UpdateInteractivityState(InteractivityState state)
        {
            InteractivityState = state;
            InteractivityStateChangedEventArgs interactivityStateChangedArgs = new InteractivityStateChangedEventArgs(InteractiveEventType.InteractivityStateChanged, state);
            _queuedEvents.Add(interactivityStateChangedArgs);
        }

        private void WriteAuthTokensToCache()
        {
            try
            {
#if !WINDOWS_UWP
                var fullPathToDataFile = string.Empty;
                if (_isUnity)
                {
                    fullPathToDataFile = _streamingAssetsPath + "/" + INTERACTIVE_DATA_FILE_NAME;
                }
                File.WriteAllText(fullPathToDataFile, "{ \"AuthToken\": \"" + _authToken + "\", \"RefreshToken\":  \"" + _oauthRefreshToken + "\"}");
#else
                var localSettings = ApplicationData.Current.LocalSettings;
                localSettings.Values["Mixer-AuthToken"] = _authToken;
                localSettings.Values["Mixer-RefreshToken"] = _oauthRefreshToken;
#endif
            }
            catch
            {
                LogError("Error: Error writing refresh tokens to local storage.");
            }
        }

        private void OnWebsocketOpen(object sender, EventArgs e)
        {
#if !WINDOWS_UWP
            _reconnectTimer.Stop();
#endif
            SendGetAllGroupsMessage();
            SendGetAllScenesMessage();
        }

#if UNITY_EDITOR
        // The following function is required because Unity has it's own certificate store. So in order for us to make
        // https calls, we need this function.
        private static bool RemoteCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // Return true if the server certificate is ok
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            bool acceptCertificate = true;
            string msg = "The server could not be validated for the following reason(s):\r\n";

            // The server did not present a certificate
            if ((sslPolicyErrors &
                 SslPolicyErrors.RemoteCertificateNotAvailable) == SslPolicyErrors.RemoteCertificateNotAvailable)
            {
                msg = msg + "\r\n    -The server did not present a certificate.\r\n";
                acceptCertificate = false;
            }
            else
            {
                // The certificate does not match the server name
                if ((sslPolicyErrors &
                     SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.RemoteCertificateNameMismatch)
                {
                    msg = msg + "\r\n    -The certificate name does not match the authenticated name.\r\n";
                    acceptCertificate = false;
                }

                // There is some other problem with the certificate
                if ((sslPolicyErrors &
                     SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.RemoteCertificateChainErrors)
                {
                    foreach (X509ChainStatus item in chain.ChainStatus)
                    {
                        if (item.Status != X509ChainStatusFlags.RevocationStatusUnknown &&
                            item.Status != X509ChainStatusFlags.OfflineRevocation)
                            break;

                        if (item.Status != X509ChainStatusFlags.NoError)
                        {
                            acceptCertificate = false;
                        }
                    }
                }
            }

            // If Validation failed, present message box
            if (acceptCertificate == false)
            {
                msg = msg + "\r\nDo you wish to override the security check?";
                acceptCertificate = true;
            }

            return acceptCertificate;
        }
#endif

        private InteractiveControl ControlFromControlID(string controlID)
        {
            var controls = Controls;
            foreach (InteractiveControl control in controls)
            {
                if (control.ControlID == controlID)
                {
                    return control;
                }
            }
            return null;
        }

        /// <summary>
        /// Trigger a cooldown, disabling the specified control for a period of time.
        /// </summary>
        /// <param name="controlID">String ID of the control to disable.</param>
        /// <param name="cooldown">Duration (in milliseconds) required between triggers.</param>
        public void TriggerCooldown(string controlID, int cooldown)
        {
            if (InteractivityState != InteractivityState.InteractivityEnabled)
            {
                throw new Exception("Error: The InteractivityManager's InteractivityState must be InteractivityEnabled before calling this method.");
            }

            if (cooldown < 1000)
            {
                Log("Info: Did you mean to use a cooldown of " + (float)cooldown / 1000 + " seconds? Remember, cooldowns are in milliseconds.");
            }

            // Get the control from our data structure to find it's etag
            string controlEtag = string.Empty;
            string controlSceneID = string.Empty;
            InteractiveControl control = ControlFromControlID(controlID);
            if (control != null)
            {
                InteractiveButtonControl button = control as InteractiveButtonControl;
                if (button != null)
                {
                    controlEtag = control.ETag;
                    if (controlEtag == string.Empty)
                    {
                        LogError("Error: Button does not have an eTag.");
                        return;
                    }
                    controlSceneID = control.SceneID;
                }
                else
                {
                    LogError("Error: The control is not a button. Only buttons have cooldowns.");
                    return;
                }
            }

            Int64 computedCooldown = (Int64)Math.Truncate(DateTime.UtcNow.ToUniversalTime().Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds + cooldown);
            var controlAsButton = control as InteractiveButtonControl;
            if (controlAsButton != null)
            {
                controlAsButton.cooldownExpirationTime = computedCooldown;
            }

            // Send an update control message
            var messageID = _currentmessageID++;
            StringBuilder stringBuilder = new StringBuilder();
            StringWriter stringWriter = new StringWriter(stringBuilder);
            using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_TYPE);
                jsonWriter.WriteValue(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ID);
                jsonWriter.WriteValue(messageID);
                jsonWriter.WritePropertyName(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WriteValue(WS_MESSAGE_METHOD_UPDATE_CONTROLS);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARAMETERS);
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_SCENE_ID);
                jsonWriter.WriteValue(controlSceneID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_CONTROLS);
                jsonWriter.WriteStartArray();
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_CONTROL_ID);
                jsonWriter.WriteValue(controlID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ETAG);
                jsonWriter.WriteValue(controlEtag);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_COOLDOWN);
                jsonWriter.WriteValue(computedCooldown);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEnd();
                SendJsonString(stringWriter.ToString());
            }
            _outstandingMessages.Add(messageID, WS_MESSAGE_METHOD_UPDATE_CONTROLS);
        }

        /// <summary>
        /// Used by the title to inform the interactivity service that it is ready to recieve interactive input.
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        public void StartInteractive()
        {
            if (InteractivityState == InteractivityState.NotInitialized)
            {
                throw new Exception("Error: InteractivityManager must be completely intialized before calling this method.");
            }
            if (InteractivityState == InteractivityState.InteractivityEnabled)
            {
                // Don't throw, just return because we are already interactive.
                return;
            }
            // We send a ready message here, but wait for a response from the server before
            // setting the interactivity state to InteractivityEnabled.
            SendReady(true);
            UpdateInteractivityState(InteractivityState.InteractivityPending);
        }

        /// <summary>
        /// Used by the title to inform the interactivity service that it is no longer receiving interactive input.
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        public void StopInteractive()
        {
            if (InteractivityState == InteractivityState.NotInitialized || 
                InteractivityState == InteractivityState.InteractivityDisabled)
            {
                return;
            }

            UpdateInteractivityState(InteractivityState.InteractivityDisabled);
            SendReady(false);
            InteractiveEventArgs stopInteractiveEvent = new InteractiveEventArgs(InteractiveEventType.InteractivityStateChanged);
            _queuedEvents.Add(stopInteractiveEvent);
        }

        /// <summary>
        /// Manages and maintains proper state updates between your game and the interactivity service.
        /// To ensure best performance, DoWork() must be called frequently, such as once per frame.
        /// Title needs to be thread safe when calling DoWork() since this is when states are changed.
        /// </summary>
        public void DoWork()
        {
            ClearPreviousControlState();

            // Go through all list of queued events and fire events.
            foreach (InteractiveEventArgs interactiveEvent in _queuedEvents.ToArray())
            {
                switch (interactiveEvent.EventType)
                {
                    case InteractiveEventType.InteractivityStateChanged:
                        if (OnInteractivityStateChanged != null)
                        {
                            OnInteractivityStateChanged(this, interactiveEvent as InteractivityStateChangedEventArgs);
                        }
                        break;
                    case InteractiveEventType.ParticipantStateChanged:
                        if (OnParticipantStateChanged != null)
                        {
                            OnParticipantStateChanged(this, interactiveEvent as InteractiveParticipantStateChangedEventArgs);
                        }
                        break;
                    case InteractiveEventType.Button:
                        if (OnInteractiveButtonEvent != null)
                        {
                            OnInteractiveButtonEvent(this, interactiveEvent as InteractiveButtonEventArgs);
                        }
                        break;
                    case InteractiveEventType.Joystick:
                        if (OnInteractiveJoystickControlEvent != null)
                        {
                            OnInteractiveJoystickControlEvent(this, interactiveEvent as InteractiveJoystickEventArgs);
                        }
                        break;
                    case InteractiveEventType.Error:
                        if (OnError != null)
                        {
                            OnError(this, interactiveEvent as InteractiveEventArgs);
                        }
                        break;
                    default:
                        // Throw exception for unexpected event type.
                        break;
                }
            }
            _queuedEvents.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            ResetInternalState();
#if !WINDOWS_UWP
            if (_refreshShortCodeTimer != null)
            {
                _refreshShortCodeTimer.Stop();
                _refreshShortCodeTimer.Elapsed -= RefreshShortCodeCallback;
            }
            if (_reconnectTimer != null)
            {
                _reconnectTimer.Stop();
                _reconnectTimer.Elapsed -= ReconnectWebsocketCallback;
            }
            if (_checkAuthStatusTimer != null)
            {
                _checkAuthStatusTimer.Stop();
                _checkAuthStatusTimer.Elapsed -= CheckAuthStatusCallback;
            }
            if (_websocket != null)
            {
                _websocket.OnOpen -= OnWebsocketOpen;
                _websocket.OnMessage -= OnWebSocketMessage;
                _websocket.OnError -= OnWebSocketError;
                _websocket.OnClose -= OnWebSocketClose;
                _websocket.Close();
            }
#else
            if (_refreshShortCodeTimer != null)
            {
                _refreshShortCodeTimer.Cancel();
            }
            if (_reconnectTimer != null)
            {
                _reconnectTimer.Cancel();
            }
            if (_checkAuthStatusTimer != null)
            {
                _checkAuthStatusTimer.Cancel();
            }
            if (_websocket != null)
            {
                _websocket.Closed -= OnWebSocketClose;
                _websocket.MessageReceived -= OnWebSocketMessage;
                _websocket.Close(0, "Dispose was called.");
            }
#endif
            _disposed = true;
        }

        public void SendMockWebSocketMessage(string rawText)
        {
            ProcessWebSocketMessage(rawText);
        }

#if !WINDOWS_UWP
        private void OnWebSocketMessage(object sender, MessageEventArgs e)
        {
            if (!e.IsText)
            {
                return;
            }
            ProcessWebSocketMessage(e.Data);
        }
#else
        private void OnWebSocketMessage(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            if (args.MessageType == SocketMessageType.Utf8)
            {
                DataReader dataReader = args.GetDataReader();
                string dataAsString = dataReader.ReadString(dataReader.UnconsumedBufferLength);
                ProcessWebSocketMessage(dataAsString);
            }
        }
#endif

#if !WINDOWS_UWP
        private void OnWebSocketError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            UpdateInteractivityState(InteractivityState.InteractivityDisabled);
            LogError(e.Message);
        }
#endif

        private void ProcessWebSocketMessage(string messageText)
        {
            try
            {
                // Figure out the message type a different way
                using (StringReader stringReader = new StringReader(messageText))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
                {
                    int messageID = -1;
                    string messageType = string.Empty;
                    while (jsonReader.Read())
                    {
                        if (jsonReader.Value != null)
                        {
                            if (jsonReader.Value.ToString() == WS_MESSAGE_KEY_ID)
                            {
                                jsonReader.ReadAsInt32();
                                messageID = Convert.ToInt32(jsonReader.Value);
                            }
                            if (jsonReader.Value.ToString() == WS_MESSAGE_KEY_TYPE)
                            {
                                jsonReader.Read();
                                if (jsonReader.Value != null)
                                {
                                    messageType = jsonReader.Value.ToString();
                                    if (messageType == WS_MESSAGE_TYPE_METHOD)
                                    {
                                        ProcessMethod(jsonReader);
                                    }
                                    else if (messageType == WS_MESSAGE_TYPE_REPLY)
                                    {
                                        ProcessReply(jsonReader, messageID);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                LogError("Error: Failed to process message: " + messageText);
            }
            Log(messageText);
        }

        private void ProcessMethod(JsonReader jsonReader)
        {
            try
            {
                while (jsonReader.Read())
                {
                    if (jsonReader.Value != null)
                    {
                        string methodName = jsonReader.Value.ToString();
                        try
                        {
                            switch (methodName)
                            {
                                case WS_MESSAGE_METHOD_PARTICIPANT_JOIN:
                                    HandleParticipantJoin(jsonReader);
                                    break;
                                case WS_MESSAGE_METHOD_PARTICIPANT_LEAVE:
                                    HandleParticipantLeave(jsonReader);
                                    break;
                                case WS_MESSAGE_METHOD_PARTICIPANT_UPDATE:
                                    HandleParticipantUpdate(jsonReader);
                                    break;
                                case WS_MESSAGE_METHOD_GIVE_INPUT:
                                    HandleGiveInput(jsonReader);
                                    break;
                                case WS_MESSAGE_METHOD_ON_READY:
                                    HandleInteractivityStarted(jsonReader);
                                    break;
                                case WS_MESSAGE_METHOD_ON_CONTROL_UPDATE:
                                    HandleControlUpdate(jsonReader);
                                    break;
                                case WS_MESSAGE_METHOD_ON_GROUP_CREATE:
                                    HandleGroupCreate(jsonReader);
                                    break;
                                case WS_MESSAGE_METHOD_ON_GROUP_UPDATE:
                                    HandleGroupUpdate(jsonReader);
                                    break;
                                case WS_MESSAGE_METHOD_ON_SCENE_CREATE:
                                    HandleSceneCreate(jsonReader);
                                    break;
                                default:
                                    // No-op. We don't throw an error because the SDK only implements a
                                    // subset of the total possible server messages so we expect to see
                                    // method messages that we don't know how to handle.
                                    break;
                            }
                        }
                        catch
                        {
                            LogError("Error: Error while processing method: " + methodName);
                        }
                    }
                }
            }
            catch
            {
                LogError("Error: Determining method.");
            }
        }

        private void ProcessReply(JsonReader jsonReader, int messageIDAsInt)
        {
            uint messageID = 0;
            if (messageIDAsInt != -1)
            {
                messageID = Convert.ToUInt32(messageIDAsInt);
            }
            else
            {
                try
                {
                    while (jsonReader.Read())
                    {
                        if (jsonReader.Value != null &&
                            jsonReader.Value.ToString() == WS_MESSAGE_KEY_ID)
                        {
                            messageID = (uint)jsonReader.ReadAsInt32();
                        }
                    }
                }
                catch
                {
                    LogError("Error: Failed to get the message ID from the reply message.");
                }
            }
            string replyMessgeMethod = string.Empty;
            _outstandingMessages.TryGetValue(messageID, out replyMessgeMethod);
            try
            {
                switch (replyMessgeMethod)
                {
                    case WS_MESSAGE_METHOD_GET_ALL_PARTICIPANTS:
                        HandleGetAllParticipants(jsonReader);
                        break;
                    case WS_MESSAGE_METHOD_GET_GROUPS:
                        HandleGetGroups(jsonReader);
                        break;
                    case WS_MESSAGE_METHOD_GET_SCENES:
                        HandleGetScenes(jsonReader);
                        break;
                    case WS_MESSAGE_METHOD_SET_CURRENT_SCENE:
                        HandlePossibleError(jsonReader);
                        break;
                    default:
                        // No-op
                        break;
                }
            }
            catch
            {
                LogError("Error: An error occured while processing the reply: " + replyMessgeMethod);
            }
        }

        private void HandlePossibleError(JsonReader jsonReader)
        {
            int errorCode = 0;
            string errorMessage = string.Empty;
            while (jsonReader.Read())
            {
                if (jsonReader.Value != null)
                {
                    string keyValue = jsonReader.Value.ToString();
                    switch (keyValue)
                    {
                        case WS_MESSAGE_KEY_ERROR_CODE:
                            jsonReader.ReadAsInt32();
                            errorCode = Convert.ToInt32(jsonReader.Value);
                            break;
                        case WS_MESSAGE_KEY_ERROR_MESSAGE:
                            jsonReader.Read();
                            if (jsonReader.Value != null)
                            {
                                errorMessage += " Message: " + jsonReader.Value.ToString();
                            }
                            break;
                        case WS_MESSAGE_KEY_ERROR_PATH:
                            jsonReader.Read();
                            if (jsonReader.Value != null)
                            {
                                errorMessage += " Path: " + jsonReader.Value.ToString();
                            }
                            break;
                        default:
                            // No-op
                            break;
                    }
                }
            }
            if (errorCode != 0 &&
                errorMessage != string.Empty)
            {
                LogError(errorMessage, errorCode);
            }
        }

        private void ResetInternalState()
        {
            _disposed = false;
            _initializedGroups = false;
            _initializedScenes = false;
            _shouldStartInteractive = false;
            _pendingConnectToWebSocket = false;
            UpdateInteractivityState(InteractivityState.NotInitialized);
        }

        private void HandleInteractivityStarted(JsonReader jsonReader)
        {
            bool startInteractive = false;
            while (jsonReader.Read())
            {
                if (jsonReader.Value != null)
                {
                    string keyValue = jsonReader.Value.ToString();
                    if (keyValue == WS_MESSAGE_KEY_ISREADY)
                    {
                        jsonReader.ReadAsBoolean();
                        if (jsonReader.Value != null)
                        {
                            startInteractive = (bool)jsonReader.Value;
                            break;
                        }
                    }
                }
            }
            if (startInteractive)
            {
                UpdateInteractivityState(InteractivityState.InteractivityEnabled);
            }
        }

        private void HandleControlUpdate(JsonReader jsonReader)
        {
            string sceneID = string.Empty;
            while (jsonReader.Read())
            {
                if (jsonReader.Value != null)
                {
                    string keyValue = jsonReader.Value.ToString();
                    if (keyValue == WS_MESSAGE_KEY_SCENE_ID)
                    {
                        jsonReader.Read();
                        sceneID = jsonReader.Value.ToString();
                    }
                    else if (keyValue == WS_MESSAGE_KEY_CONTROLS)
                    {
                        UpdateControls(jsonReader, sceneID);
                    }
                }
            }
        }

        private void UpdateControls(JsonReader jsonReader, string sceneID)
        {
            try
            {
                while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
                {
                    if (jsonReader.TokenType == JsonToken.StartObject)
                    {
                        var updatedControl = ReadControl(jsonReader, sceneID);
                        InteractiveControl oldControl = null;
                        var controls = Controls;
                        foreach (InteractiveControl control in controls)
                        {
                            if (control.ControlID == updatedControl.ControlID)
                            {
                                oldControl = control;
                                break;
                            }
                        }
                        var controlAsButton = updatedControl as InteractiveButtonControl;
                        if (controlAsButton != null)
                        {
                            var oldButtonControl = oldControl as InteractiveButtonControl;
                            if (oldButtonControl != null)
                            {
                                _buttons.Remove(oldButtonControl);
                            }
                            _buttons.Add(controlAsButton);
                        }
                        var controlAsJoystick = updatedControl as InteractiveJoystickControl;
                        if (controlAsJoystick != null)
                        {
                            var oldJoystickControl = oldControl as InteractiveJoystickControl;
                            if (oldJoystickControl != null)
                            {
                                _joysticks.Remove(oldJoystickControl);
                            }
                            _joysticks.Add(controlAsJoystick);
                        }
                        if (oldControl != null)
                        {
                            _controls.Remove(oldControl);
                        }
                        _controls.Add(updatedControl);
                    }
                }
            }
            catch
            {
                LogError("Error: Failed reading controls for scene: " + sceneID + ".");
            }
        }

        private void HandleSceneCreate(JsonReader jsonReader)
        {
            while (jsonReader.Read())
            {
                if (jsonReader.Value != null)
                {
                    string keyValue = jsonReader.Value.ToString();
                    if (keyValue == WS_MESSAGE_KEY_SCENES)
                    {
                        _scenes.Add(ReadScene(jsonReader));
                    }
                }
            }
        }

        private void HandleGroupCreate(JsonReader jsonReader)
        {
            ProcessGroups(jsonReader);
        }

        private void HandleGroupUpdate(JsonReader jsonReader)
        {
            ProcessGroups(jsonReader);
        }

        private void ProcessGroups(JsonReader jsonReader)
        {
            while (jsonReader.Read())
            {
                if (jsonReader.Value != null)
                {
                    string keyValue = jsonReader.Value.ToString();
                    if (keyValue == WS_MESSAGE_KEY_GROUPS)
                    {
                        var newGroup = ReadGroup(jsonReader);
                        var groups = Groups;
                        int existingGroupIndex = -1;
                        for (int i = 0; i < groups.Count; i++)
                        {
                            InteractiveGroup group = groups[i];
                            if (group.GroupID == newGroup.GroupID)
                            {
                                existingGroupIndex = i;
                                break;
                            }
                        }
                        if (existingGroupIndex != -1)
                        {
                            CloneGroupValues(newGroup, groups[existingGroupIndex]);
                        }
                        else
                        {
                            _groups.Add(newGroup);
                        }
                    }
                }
            }
        }

        private void CloneGroupValues(InteractiveGroup source, InteractiveGroup destination)
        {
            destination.etag = source.etag;
            destination.SceneID = source.SceneID;
            destination.GroupID = source.GroupID;
        }

        private void HandleGetAllParticipants(JsonReader jsonReader)
        {
            while (jsonReader.Read())
            {
                if (jsonReader.TokenType == JsonToken.StartObject)
                {
                    _participants.Add(ReadParticipant(jsonReader));
                }
            }
        }

        private List<InteractiveParticipant> ReadParticipants(JsonReader jsonReader)
        {
            List<InteractiveParticipant> participants = new List<InteractiveParticipant>();
            while (jsonReader.Read())
            {
                if (jsonReader.TokenType == JsonToken.StartObject)
                {
                    InteractiveParticipant newParticipant = ReadParticipant(jsonReader);
                    var existingParticipants = Participants;
                    int existingParticipantIndex = -1;
                    for (int i = 0; i < existingParticipants.Count; i++)
                    {
                        InteractiveParticipant participant = existingParticipants[i];
                        if (participant.UserID == newParticipant.UserID)
                        {
                            existingParticipantIndex = i;
                        }
                    }
                    if (existingParticipantIndex != -1)
                    {
                        CloneParticipantValues(existingParticipants[existingParticipantIndex], newParticipant);
                    }
                    else
                    {
                        _participants.Add(newParticipant);
                    }
                    participants.Add(newParticipant);
                }
            }
            return participants;
        }

        private void CloneParticipantValues(InteractiveParticipant source, InteractiveParticipant destination)
        {
            destination.sessionID = source.sessionID;
            destination.UserID = source.UserID;
            destination.UserName = source.UserName;
            destination.Level = source.Level;
            destination.LastInputAt = source.LastInputAt;
            destination.ConnectedAt = source.ConnectedAt;
            destination.InputDisabled = source.InputDisabled;
            destination.State = source.State;
            destination.groupID = source.groupID;
            destination.etag = source.etag;
        }

        private InteractiveParticipant ReadParticipant(JsonReader jsonReader)
        {
            uint Id = 0;
            string sessionID = string.Empty;
            string etag = string.Empty;
            string interactiveUserName = string.Empty;
            string groupID = string.Empty;
            uint interactiveLevel = 0;
            bool inputDisabled = false;
            DateTime lastInputAt = new DateTime();
            DateTime connectedAt = new DateTime();
            int startDepth = jsonReader.Depth;
            while (jsonReader.Read() && jsonReader.Depth > startDepth)
            {
                if (jsonReader.Value != null)
                {
                    if (jsonReader.Value != null)
                    {
                        string keyValue = jsonReader.Value.ToString();
                        switch (keyValue)
                        {
                            case WS_MESSAGE_KEY_SESSION_ID:
                                jsonReader.Read();
                                if (jsonReader.Value != null)
                                {
                                    sessionID = jsonReader.Value.ToString();
                                }
                                break;
                            case WS_MESSAGE_KEY_ETAG:
                                jsonReader.Read();
                                if (jsonReader.Value != null)
                                {
                                    etag = jsonReader.Value.ToString();
                                }
                                break;
                            case WS_MESSAGE_KEY_USER_ID:
                                jsonReader.ReadAsInt32();
                                Id = Convert.ToUInt32(jsonReader.Value);
                                break;
                            case WS_MESSAGE_KEY_USERNAME:
                                jsonReader.Read();
                                interactiveUserName = jsonReader.Value.ToString();
                                break;
                            case WS_MESSAGE_KEY_LEVEL:
                                jsonReader.Read();
                                interactiveLevel = Convert.ToUInt32(jsonReader.Value);
                                break;
                            case WS_MESSAGE_KEY_LAST_INPUT_AT:
                                jsonReader.Read();
                                DateTime.TryParse(jsonReader.Value.ToString(), out lastInputAt);
                                break;
                            case WS_MESSAGE_KEY_CONNECTED_AT:
                                jsonReader.Read();
                                DateTime.TryParse(jsonReader.Value.ToString(), out connectedAt);
                                break;
                            case WS_MESSAGE_KEY_GROUP_ID:
                                jsonReader.Read();
                                groupID = jsonReader.Value.ToString();
                                break;
                            case WS_MESSAGE_KEY_DISABLED:
                                jsonReader.ReadAsBoolean();
                                inputDisabled = (bool)jsonReader.Value;
                                break;
                            default:
                                // No-op
                                break;
                        }
                    }
                }
            }
            InteractiveParticipantState participantState = inputDisabled ? InteractiveParticipantState.InputDisabled : InteractiveParticipantState.Joined;
            return new InteractiveParticipant(sessionID, etag, Id, groupID, interactiveUserName, interactiveLevel, lastInputAt, connectedAt, inputDisabled, participantState);
        }

        private void HandleGetGroups(JsonReader jsonReader)
        {
            while (jsonReader.Read())
            {
                if (jsonReader.Value != null &&
                    jsonReader.Value.ToString() == WS_MESSAGE_KEY_GROUPS)
                {
                    var newGroup = ReadGroup(jsonReader);
                    var groups = Groups;
                    int existingGroupIndex = -1;
                    for (int i = 0; i < groups.Count; i++)
                    {
                        InteractiveGroup group = groups[i];
                        if (group.GroupID == newGroup.GroupID)
                        {
                            existingGroupIndex = i;
                            break;
                        }
                    }
                    if (existingGroupIndex != -1)
                    {
                        CloneGroupValues(newGroup, groups[existingGroupIndex]);
                    }
                    else
                    {
                        _groups.Add(newGroup);
                    }
                }
            }
            _initializedGroups = true;
            if (_initializedGroups &&
                _initializedScenes)
            {
                UpdateInteractivityState(InteractivityState.Initialized);
                if (_shouldStartInteractive)
                {
                    StartInteractive();
                }
            }
        }

        private InteractiveGroup ReadGroup(JsonReader jsonReader)
        {
            int startDepth = jsonReader.Depth;
            string etag = string.Empty;
            string sceneID = string.Empty;
            string groupID = string.Empty;
            jsonReader.Read();
            while (jsonReader.Read() && jsonReader.Depth > startDepth)
            {
                if (jsonReader.Value != null)
                {
                    string keyValue = jsonReader.Value.ToString();
                    switch (keyValue)
                    {
                        case WS_MESSAGE_KEY_ETAG:
                            jsonReader.ReadAsString();
                            if (jsonReader.Value != null)
                            {
                                etag = jsonReader.Value.ToString();
                            }
                            break;
                        case WS_MESSAGE_KEY_SCENE_ID:
                            jsonReader.ReadAsString();
                            if (jsonReader.Value != null)
                            {
                                sceneID = jsonReader.Value.ToString();
                            }
                            break;
                        case WS_MESSAGE_KEY_GROUP_ID:
                            jsonReader.ReadAsString();
                            if (jsonReader.Value != null)
                            {
                                groupID = jsonReader.Value.ToString();
                            }
                            break;
                        default:
                            // No-op
                            break;
                    }
                }
            }
            return new InteractiveGroup(etag, sceneID, groupID);
        }

        private void HandleGetScenes(JsonReader jsonReader)
        {
            while (jsonReader.Read())
            {
                if (jsonReader.Value != null)
                {
                    string keyValue = jsonReader.Value.ToString();
                    if (keyValue == WS_MESSAGE_KEY_SCENES)
                    {
                        _scenes = ReadScenes(jsonReader);
                    }
                }
            }
            _initializedScenes = true;
            if (_initializedGroups &&
                _initializedScenes)
            {
                UpdateInteractivityState(InteractivityState.Initialized);
                if (_shouldStartInteractive)
                {
                    StartInteractive();
                }
            }
        }

        private List<InteractiveScene> ReadScenes(JsonReader jsonReader)
        {
            List<InteractiveScene> scenes = new List<InteractiveScene>();
            while (jsonReader.Read())
            {
                if (jsonReader.TokenType == JsonToken.StartObject)
                {
                    scenes.Add(ReadScene(jsonReader));
                }
            }
            return scenes;
        }

        private InteractiveScene ReadScene(JsonReader jsonReader)
        {
            InteractiveScene scene = new InteractiveScene();
            try
            {
                int startDepth = jsonReader.Depth;
                while (jsonReader.Read() && jsonReader.Depth > startDepth)
                {
                    if (jsonReader.Value != null)
                    {
                        string keyValue = jsonReader.Value.ToString();
                        switch (keyValue)
                        {
                            case WS_MESSAGE_KEY_SCENE_ID:
                                jsonReader.ReadAsString();
                                if (jsonReader.Value != null)
                                {
                                    scene.SceneID = jsonReader.Value.ToString();
                                }
                                break;
                            case WS_MESSAGE_KEY_ETAG:
                                jsonReader.ReadAsString();
                                if (jsonReader.Value != null)
                                {
                                    scene.etag = jsonReader.Value.ToString();
                                }
                                break;
                            case WS_MESSAGE_KEY_CONTROLS:
                                ReadControls(jsonReader, scene);
                                break;
                            default:
                                // No-op
                                break;
                        }
                    }
                }
            }
            catch
            {
                LogError("Error: Error reading scene " + scene.SceneID + ".");
            }

            return scene;
        }

        private void ReadControls(JsonReader jsonReader, InteractiveScene scene)
        {
            try
            {
                while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
                {
                    if (jsonReader.TokenType == JsonToken.StartObject)
                    {
                        // Add the control to the scenes' controls & the global list of controls.
                        var control = ReadControl(jsonReader, scene.SceneID);
                        var controlAsButton = control as InteractiveButtonControl;
                        if (controlAsButton != null)
                        {
                            _buttons.Add(controlAsButton);
                        }
                        var controlAsJoystick = control as InteractiveJoystickControl;
                        if (controlAsJoystick != null)
                        {
                            _joysticks.Add(controlAsJoystick);
                        }
                        _controls.Add(control);
                    }
                }
            }
            catch
            {
                LogError("Error: Failed reading controls for scene: " + scene.SceneID + ".");
            }
        }

        private InteractiveControl ReadControl(JsonReader jsonReader, string sceneID = "")
        {
            InteractiveControl newControl;
            int startDepth = jsonReader.Depth;
            string controlID = string.Empty;
            bool disabled = false;
            string helpText = string.Empty;
            string eTag = string.Empty;
            string kind = string.Empty;
            try
            {
                while (jsonReader.Read() && jsonReader.Depth > startDepth)
                {
                    if (jsonReader.Value != null)
                    {
                        string keyValue = jsonReader.Value.ToString();
                        switch (keyValue)
                        {
                            case WS_MESSAGE_KEY_CONTROL_ID:
                                jsonReader.ReadAsString();
                                controlID = jsonReader.Value.ToString();
                                break;
                            case WS_MESSAGE_KEY_DISABLED:
                                jsonReader.ReadAsBoolean();
                                disabled = (bool)jsonReader.Value;
                                break;
                            case WS_MESSAGE_KEY_TEXT:
                                jsonReader.Read();
                                helpText = jsonReader.Value.ToString();
                                break;
                            case WS_MESSAGE_KEY_ETAG:
                                jsonReader.Read();
                                eTag = jsonReader.Value.ToString();
                                break;
                            case WS_MESSAGE_KEY_KIND:
                                jsonReader.Read();
                                kind = jsonReader.Value.ToString();
                                break;
                            default:
                                // No-op
                                break;
                        }
                    }
                }
            }
            catch
            {
                LogError("Error: Error reading control " + controlID + ".");
            }
            if (kind == WS_MESSAGE_VALUE_CONTROL_TYPE_BUTTON)
            {
                newControl = new InteractiveButtonControl(controlID, disabled, helpText, eTag, sceneID);
            }
            else if (kind == WS_MESSAGE_VALUE_CONTROL_TYPE_JOYSTICK)
            {
                newControl = new InteractiveJoystickControl(controlID, disabled, helpText, eTag, sceneID);
            }
            else
            {
                newControl = new InteractiveControl(controlID, disabled, helpText, eTag, sceneID);
            }
            return newControl;
        }

        private List<InputEvent> ReadInputs(JsonReader jsonReader)
        {
            List<InputEvent> inputs = new List<InputEvent>();
            while (jsonReader.Read())
            {
                if (jsonReader.TokenType == JsonToken.StartObject)
                {
                    inputs.Add(ReadInput(jsonReader));
                }
            }
            return inputs;
        }

        private InputEvent ReadInput(JsonReader jsonReader)
        {
            int startDepth = jsonReader.Depth;
            string controlID = string.Empty;
            InteractiveEventType type = InteractiveEventType.Button;
            bool isPressed = false;
            float x = 0;
            float y = 0;
            try
            {
                while (jsonReader.Read() && jsonReader.Depth > startDepth)
                {
                    if (jsonReader.Value != null)
                    {
                        string keyValue = jsonReader.Value.ToString();
                        switch (keyValue)
                        {
                            case WS_MESSAGE_KEY_CONTROL_ID:
                                jsonReader.ReadAsString();
                                if (jsonReader.Value != null)
                                {
                                    controlID = jsonReader.Value.ToString();
                                }
                                break;
                            case WS_MESSAGE_KEY_EVENT:
                                string eventValue = jsonReader.ReadAsString();
                                if (eventValue == "mousedown" || eventValue == "mouseup")
                                {
                                    type = InteractiveEventType.Button;
                                    if (eventValue == "mousedown")
                                    {
                                        isPressed = true;
                                    }
                                    else
                                    {
                                        isPressed = false;
                                    }
                                }
                                else if (eventValue == "move")
                                {
                                    type = InteractiveEventType.Joystick;
                                }
                                break;
                            case WS_MESSAGE_KEY_X:
                                x = (float)jsonReader.ReadAsDouble();
                                break;
                            case WS_MESSAGE_KEY_Y:
                                y = (float)jsonReader.ReadAsDouble();
                                break;
                            default:
                                // No-op
                                break;
                        }
                    }
                }
            }
            catch
            {
                LogError("Error: Error reading input from control " + controlID + ".");
            }
            return new InputEvent(controlID, type, isPressed, x, y);
        }

        private void HandleParticipantJoin(JsonReader jsonReader)
        {
            int startDepth = jsonReader.Depth;
            while (jsonReader.Read())
            {
                if (jsonReader.Value != null)
                {
                    string keyValue = jsonReader.Value.ToString();
                    switch (keyValue)
                    {
                        case WS_MESSAGE_KEY_PARTICIPANTS:
                            List<InteractiveParticipant> participants = ReadParticipants(jsonReader);
                            for (int i = 0; i < participants.Count; i++)
                            {
                                InteractiveParticipant newParticipant = participants[i];
                                newParticipant.State = InteractiveParticipantState.Joined;
                                _queuedEvents.Add(new InteractiveParticipantStateChangedEventArgs(InteractiveEventType.ParticipantStateChanged, newParticipant, newParticipant.State));
                            }
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        private void HandleParticipantLeave(JsonReader jsonReader)
        {
            try
            {
                int startDepth = jsonReader.Depth;
                while (jsonReader.Read())
                {
                    if (jsonReader.Value != null)
                    {
                        string keyValue = jsonReader.Value.ToString();
                        switch (keyValue)
                        {
                            case WS_MESSAGE_KEY_PARTICIPANTS:
                                List<InteractiveParticipant> participants = ReadParticipants(jsonReader);
                                for (int i = 0; i < participants.Count; i++)
                                {
                                    for (int j = _participants.Count - 1; j >= 0; j--)
                                    {
                                        if (_participants[j].UserID == participants[i].UserID)
                                        {
                                            InteractiveParticipant participant = _participants[j];
                                            participant.State = InteractiveParticipantState.Left;
                                            _queuedEvents.Add(new InteractiveParticipantStateChangedEventArgs(InteractiveEventType.ParticipantStateChanged, participant, participant.State));
                                        }
                                    }
                                }
                                break;
                            default:
                                break;
                        }
                    }
                }
            }
            catch
            {
                LogError("Error: Error while processing participant leave message.");
            }
        }

        private void HandleParticipantUpdate(JsonReader jsonReader)
        {
            int startDepth = jsonReader.Depth;
            while (jsonReader.Read())
            {
                if (jsonReader.Value != null)
                {
                    string keyValue = jsonReader.Value.ToString();
                    switch (keyValue)
                    {
                        case WS_MESSAGE_KEY_PARTICIPANTS:
                            ReadParticipants(jsonReader);
                            break;
                        default:
                            break;
                    }
                }
            }
        }
        internal struct InputEvent
        {
            internal string controlID;
            internal InteractiveEventType Type;
            internal bool IsPressed;
            internal float X;
            internal float Y;

            internal InputEvent(string cntrlID, InteractiveEventType type, bool isPressed, float x, float y)
            {
                controlID = cntrlID;
                Type = type;
                IsPressed = isPressed;
                X = x;
                Y = y;
            }
        };

        private void HandleGiveInput(JsonReader jsonReader)
        {
            string participantSessionID = string.Empty;
            List<InputEvent> inputEvents = new List<InputEvent>();
            while (jsonReader.Read())
            {
                if (jsonReader.Value != null)
                {
                    var value = jsonReader.Value.ToString();
                    switch (value)
                    {
                        case WS_MESSAGE_KEY_PARTICIPANT_ID:
                            jsonReader.Read();
                            participantSessionID = jsonReader.Value.ToString();
                            break;
                        case WS_MESSAGE_KEY_INPUT:
                            inputEvents = ReadInputs(jsonReader);
                            break;
                        default:
                            // No-op
                            break;
                    }
                }
            }
            InteractiveParticipant participant = ParticipantBySessionId(participantSessionID);
            // The following allows the Unity Interactive Editor to simulate input.
            if (useMockData && _participants.Count > 0)
            {
                participant = _participants[0];
            }
            participant.LastInputAt = DateTime.UtcNow;
            foreach (InputEvent inputEvent in inputEvents)
            {
                if (inputEvent.Type == InteractiveEventType.Button)
                {
                    InteractiveButtonEventArgs eventArgs = new InteractiveButtonEventArgs(inputEvent.Type, inputEvent.controlID, participant, inputEvent.IsPressed);
                    _queuedEvents.Add(eventArgs);
                    UpdateInternalButtonState(eventArgs);
                }
                else if (inputEvent.Type == InteractiveEventType.Joystick)
                {
                    InteractiveJoystickEventArgs eventArgs = new InteractiveJoystickEventArgs(inputEvent.Type, inputEvent.controlID, participant, inputEvent.X, inputEvent.Y);
                    _queuedEvents.Add(eventArgs);
                    UpdateInternalJoystickState(eventArgs);
                }
            }
        }

        private InteractiveParticipant ParticipantBySessionId(string sessionID)
        {
            InteractiveParticipant target = null;
            var existingParticipants = Participants;
            foreach (InteractiveParticipant participant in existingParticipants)
            {
                if (participant.sessionID == sessionID)
                {
                    target = participant;
                    break;
                }
            }
            return target;
        }

        internal bool GetButtonDown(string controlID, uint userID)
        {
            bool getButtonDownResult = false;
            bool participantExists = false;
            Dictionary<string, InternalButtonState> participantControls;
            participantExists = _buttonStatesByParticipant.TryGetValue(userID, out participantControls);
            if (participantExists)
            {
                bool controlExists = false;
                InternalButtonState buttonState;
                controlExists = participantControls.TryGetValue(controlID, out buttonState);
                if (controlExists)
                {
                    getButtonDownResult = buttonState.ButtonCountState.CountOfButtonDownEvents > 0;
                }
            }
            else
            {
                getButtonDownResult = false;
            }
            return getButtonDownResult;
        }

        internal bool GetButtonPressed(string controlID, uint userID)
        {
            bool getButtonResult = false;
            bool participantExists = false;
            Dictionary<string, InternalButtonState> participantControls;
            participantExists = _buttonStatesByParticipant.TryGetValue(userID, out participantControls);
            if (participantExists)
            {
                bool controlExists = false;
                InternalButtonState buttonState;
                controlExists = participantControls.TryGetValue(controlID, out buttonState);
                if (controlExists)
                {
                    getButtonResult = buttonState.ButtonCountState.CountOfButtonPressEvents > 0;
                }
            }
            else
            {
                getButtonResult = false;
            }
            return getButtonResult;
        }

        internal bool GetButtonUp(string controlID, uint userID)
        {
            bool getButtonUpResult = false;
            bool participantExists = false;
            Dictionary<string, InternalButtonState> participantControls;
            participantExists = _buttonStatesByParticipant.TryGetValue(userID, out participantControls);
            if (participantExists)
            {
                bool controlExists = false;
                InternalButtonState buttonState;
                controlExists = participantControls.TryGetValue(controlID, out buttonState);
                if (controlExists)
                {
                    getButtonUpResult = buttonState.ButtonCountState.CountOfButtonUpEvents > 0;
                }
            }
            else
            {
                getButtonUpResult = false;
            }
            return getButtonUpResult;
        }

        internal uint GetCountOfButtonDowns(string controlID, uint userID)
        {
            uint countOfButtonDownEvents = 0;
            bool participantExists = false;
            Dictionary<string, InternalButtonState> participantControls;
            participantExists = _buttonStatesByParticipant.TryGetValue(userID, out participantControls);
            if (participantExists)
            {
                bool controlExists = false;
                InternalButtonState buttonState;
                controlExists = participantControls.TryGetValue(controlID, out buttonState);
                if (controlExists)
                {
                    countOfButtonDownEvents = buttonState.ButtonCountState.CountOfButtonDownEvents;
                }
            }
            return countOfButtonDownEvents;
        }

        internal uint GetCountOfButtonPresses(string controlID, uint userID)
        {
            uint countOfButtonPressEvents = 0;
            bool participantExists = false;
            Dictionary<string, InternalButtonState> participantControls;
            participantExists = _buttonStatesByParticipant.TryGetValue(userID, out participantControls);
            if (participantExists)
            {
                bool controlExists = false;
                InternalButtonState buttonState;
                controlExists = participantControls.TryGetValue(controlID, out buttonState);
                if (controlExists)
                {
                    countOfButtonPressEvents = buttonState.ButtonCountState.CountOfButtonPressEvents;
                }
            }
            return countOfButtonPressEvents;
        }

        internal uint GetCountOfButtonUps(string controlID, uint userID)
        {
            uint countOfButtonUpEvents = 0;
            InternalButtonState buttonState;
            bool participantExists = false;
            Dictionary<string, InternalButtonState> participantControls;
            participantExists = _buttonStatesByParticipant.TryGetValue(userID, out participantControls);
            if (participantExists)
            {
                bool controlExists = false;
                controlExists = participantControls.TryGetValue(controlID, out buttonState);
                if (controlExists)
                {
                    countOfButtonUpEvents = buttonState.ButtonCountState.CountOfButtonUpEvents;
                }
            }
            return countOfButtonUpEvents;
        }

        internal bool TryGetButtonStateByParticipant(uint userID, string controlID, out InternalButtonState buttonState)
        {
            buttonState = new InternalButtonState();
            bool buttonExists = false;
            bool participantExists = false;
            Dictionary<string, InternalButtonState> participantControls;
            participantExists = _buttonStatesByParticipant.TryGetValue(userID, out participantControls);
            if (participantExists)
            {
                bool controlExists = false;
                controlExists = participantControls.TryGetValue(controlID, out buttonState);
                if (controlExists)
                {
                    buttonExists = true;
                }
            }
            return buttonExists;
        }

        internal InteractiveJoystickControl GetJoystick(string controlID, uint userID)
        {
            InteractiveJoystickControl joystick = new InteractiveJoystickControl(controlID, true, string.Empty, string.Empty, string.Empty);
            var joysticks = Joysticks;
            foreach (InteractiveJoystickControl potential in joysticks)
            {
                if (potential.ControlID == controlID)
                {
                    joystick = potential;
                }
            }
            joystick.UserID = userID;
            return joystick;
        }

        internal double GetJoystickX(string controlID, uint userID)
        {
            double joystickX = 0;
            InternalJoystickState joystickState;
            if (TryGetJoystickStateByParticipant(userID, controlID, out joystickState))
            {
                joystickX = joystickState.X;
            }
            return joystickX;
        }

        internal double GetJoystickY(string controlID, uint userID)
        {
            double joystickY = 0;
            InternalJoystickState joystickState;
            if (TryGetJoystickStateByParticipant(userID, controlID, out joystickState))
            {
                joystickY = joystickState.Y;
            }
            return joystickY;
        }

        private bool TryGetJoystickStateByParticipant(uint userID, string controlID, out InternalJoystickState joystickState)
        {
            joystickState = new InternalJoystickState();
            bool joystickExists = false;
            bool participantExists = false;
            Dictionary<string, InternalJoystickState> participantControls;
            participantExists = _joystickStatesByParticipant.TryGetValue(userID, out participantControls);
            if (participantExists)
            {
                bool controlExists = false;
                controlExists = participantControls.TryGetValue(controlID, out joystickState);
                if (controlExists)
                {
                    joystickExists = true;
                }
            }
            return joystickExists;
        }

        internal InteractiveControl GetControl(string controlID)
        {
            InteractiveControl control = new InteractiveControl(controlID, true, "", "", "");
            var controls = Controls;
            foreach (InteractiveControl currentControl in controls)
            {
                if (currentControl.ControlID == controlID)
                {
                    control = currentControl;
                    break;
                }
            }
            return control;
        }

        /// <summary>
        /// Gets a button control object by ID.
        /// </summary>
        /// <param name="controlID">The ID of the control.</param>
        /// <returns></returns>
        public InteractiveButtonControl GetButton(string controlID)
        {
            InteractiveButtonControl buttonControl = new InteractiveButtonControl(controlID, false, string.Empty, string.Empty, string.Empty);
            var buttons = Buttons;
            foreach (InteractiveButtonControl currentButtonControl in buttons)
            {
                if (currentButtonControl.ControlID == controlID)
                {
                    buttonControl = currentButtonControl;
                    break;
                }
            }
            return buttonControl;
        }

        /// <summary>
        /// Gets a joystick control object by ID.
        /// </summary>
        /// <param name="controlID">The ID of the control.</param>
        /// <returns></returns>
        public InteractiveJoystickControl GetJoystick(string controlID)
        {
            InteractiveJoystickControl joystickControl = new InteractiveJoystickControl(controlID, true, "", "", "");
            var joysticks = Joysticks;
            foreach (InteractiveJoystickControl currentJoystick in joysticks)
            {
                if (currentJoystick.ControlID == controlID)
                {
                    joystickControl = currentJoystick;
                    break;
                }
            }
            return joystickControl;
        }

        /// <summary>
        /// Gets the current scene for the default group.
        /// </summary>
        /// <returns></returns>
        public string GetCurrentScene()
        {
            InteractiveGroup group = GroupFromID(WS_MESSAGE_VALUE_DEFAULT_GROUP_ID);
            return group.SceneID;
        }

        /// <summary>
        /// Sets the current scene for the default group.
        /// </summary>
        /// <param name="sceneID">The ID of the scene to change to.</param>
        public void SetCurrentScene(string sceneID)
        {
            InteractiveGroup defaultGroup = GroupFromID(WS_MESSAGE_VALUE_DEFAULT_GROUP_ID);
            if (defaultGroup != null)
            {
                defaultGroup.SetScene(sceneID);
            }
        }

        internal void SetCurrentSceneInternal(InteractiveGroup group, string sceneID)
        {
            SendSetUpdateGroupsMessage(group.GroupID, sceneID, group.etag);
        }

        private InteractiveGroup GroupFromID(string groupID)
        {
            InteractiveGroup target = new InteractiveGroup("", groupID, WS_MESSAGE_VALUE_DEFAULT_GROUP_ID);
            var groups = Groups;
            foreach (InteractiveGroup group in groups)
            {
                if (group.GroupID == groupID)
                {
                    target = group;
                    break;
                }
            }
            return target;
        }

        private InteractiveScene SceneFromID(string sceneID)
        {
            InteractiveScene target = new InteractiveScene(sceneID);
            var scenes = Scenes;
            foreach (InteractiveScene scene in scenes)
            {
                if (scene.SceneID == sceneID)
                {
                    target = scene;
                    break;
                }
            }
            return target;
        }

        // Private methods to send WebSocket messages
        private void SendReady(bool isReady)
        {
            uint messageID = _currentmessageID++;
            StringBuilder stringBuilder = new StringBuilder();
            StringWriter stringWriter = new StringWriter(stringBuilder);
            using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_TYPE);
                jsonWriter.WriteValue(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ID);
                jsonWriter.WriteValue(messageID);
                jsonWriter.WritePropertyName(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WriteValue(WS_MESSAGE_METHOD_READY);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARAMETERS);
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(READY_PARAMETER_IS_READY);
                jsonWriter.WriteValue(isReady);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEnd();
                SendJsonString(stringWriter.ToString());
            }
            _outstandingMessages.Add(messageID, WS_MESSAGE_METHOD_READY);
        }

        internal void SendCreateGroupsMessage(string groupID, string sceneID)
        {
            var messageID = _currentmessageID++;
            StringBuilder stringBuilder = new StringBuilder();
            StringWriter stringWriter = new StringWriter(stringBuilder);
            using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_TYPE);
                jsonWriter.WriteValue(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ID);
                jsonWriter.WriteValue(messageID);
                jsonWriter.WritePropertyName(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WriteValue(WS_MESSAGE_METHOD_CREATE_GROUPS);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARAMETERS);
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_GROUPS);
                jsonWriter.WriteStartArray();
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_GROUP_ID);
                jsonWriter.WriteValue(groupID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_SCENE_ID);
                jsonWriter.WriteValue(sceneID);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEnd();
                SendJsonString(stringWriter.ToString());
            }
            _outstandingMessages.Add(messageID, WS_MESSAGE_METHOD_SET_CURRENT_SCENE);
        }

        internal void SendSetUpdateGroupsMessage(string groupID, string sceneID, string groupEtag)
        {
            var messageID = _currentmessageID++;
            StringBuilder stringBuilder = new StringBuilder();
            StringWriter stringWriter = new StringWriter(stringBuilder);
            using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_TYPE);
                jsonWriter.WriteValue(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ID);
                jsonWriter.WriteValue(messageID);
                jsonWriter.WritePropertyName(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WriteValue(WS_MESSAGE_METHOD_UPDATE_GROUPS);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARAMETERS);
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_GROUPS);
                jsonWriter.WriteStartArray();
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_GROUP_ID);
                jsonWriter.WriteValue(WS_MESSAGE_VALUE_DEFAULT_GROUP_ID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_SCENE_ID);
                jsonWriter.WriteValue(sceneID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ETAG);
                jsonWriter.WriteValue(groupEtag);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEnd();
                SendJsonString(stringWriter.ToString());
            }
            _outstandingMessages.Add(messageID, WS_MESSAGE_METHOD_SET_CURRENT_SCENE);
        }

        internal void SendSetUpdateScenesMessage(InteractiveScene scene)
        {
            var messageID = _currentmessageID++;
            StringBuilder stringBuilder = new StringBuilder();
            StringWriter stringWriter = new StringWriter(stringBuilder);
            using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_TYPE);
                jsonWriter.WriteValue(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ID);
                jsonWriter.WriteValue(messageID);
                jsonWriter.WritePropertyName(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WriteValue(WS_MESSAGE_METHOD_UPDATE_SCENES);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARAMETERS);
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_SCENES);
                jsonWriter.WriteStartArray();
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_SCENE_ID);
                jsonWriter.WriteValue(scene.SceneID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ETAG);
                jsonWriter.WriteValue(scene.etag);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEnd();
                SendJsonString(stringWriter.ToString());
            }
            _outstandingMessages.Add(messageID, WS_MESSAGE_METHOD_SET_CURRENT_SCENE);
        }

        internal void SendUpdateParticipantsMessage(InteractiveParticipant participant)
        {
            var messageID = _currentmessageID++;
            StringBuilder stringBuilder = new StringBuilder();
            StringWriter stringWriter = new StringWriter(stringBuilder);
            using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_TYPE);
                jsonWriter.WriteValue(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ID);
                jsonWriter.WriteValue(messageID);
                jsonWriter.WritePropertyName(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WriteValue(WS_MESSAGE_METHOD_UPDATE_PARTICIPANTS);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARAMETERS);
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARTICIPANTS);
                jsonWriter.WriteStartArray();
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_SESSION_ID);
                jsonWriter.WriteValue(participant.sessionID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ETAG);
                jsonWriter.WriteValue(participant.etag);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_GROUP_ID);
                jsonWriter.WriteValue(participant.groupID);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEnd();
                SendJsonString(stringWriter.ToString());
            }
            _outstandingMessages.Add(messageID, WS_MESSAGE_METHOD_UPDATE_PARTICIPANTS);
        }

        private void SendSetCompressionMessage()
        {
            var messageID = _currentmessageID++;
            StringBuilder stringBuilder = new StringBuilder();
            StringWriter stringWriter = new StringWriter(stringBuilder);
            using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_TYPE);
                jsonWriter.WriteValue(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ID);
                jsonWriter.WriteValue(messageID);
                jsonWriter.WritePropertyName(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WriteValue(WS_MESSAGE_METHOD_SET_COMPRESSION);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARAMETERS);
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_SCHEME);
                jsonWriter.WriteStartArray();
                jsonWriter.WriteValue(COMPRESSION_TYPE_GZIP);
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEnd();
                SendJsonString(stringWriter.ToString());
            }
            _outstandingMessages.Add(messageID, WS_MESSAGE_METHOD_SET_COMPRESSION);
        }

        internal void SendSetControlEnabled(string controlID, bool disabled)
        {
            InteractiveControl control = ControlFromControlID(controlID);
            if (control == null)
            {
                return;
            }
            var messageID = _currentmessageID++;
            StringBuilder stringBuilder = new StringBuilder();
            StringWriter stringWriter = new StringWriter(stringBuilder);
            using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_TYPE);
                jsonWriter.WriteValue(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ID);
                jsonWriter.WriteValue(messageID);
                jsonWriter.WritePropertyName(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WriteValue(WS_MESSAGE_METHOD_UPDATE_CONTROLS);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARAMETERS);
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_SCENE_ID);
                jsonWriter.WriteValue(control.SceneID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_CONTROLS);
                jsonWriter.WriteStartArray();
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_CONTROL_ID);
                jsonWriter.WriteValue(controlID);
                jsonWriter.WritePropertyName(WS_MESSAGE_VALUE_DISABLED);
                jsonWriter.WriteValue(disabled);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ETAG);
                jsonWriter.WriteValue(control.ETag);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEnd();
                SendJsonString(stringWriter.ToString());
            }
            _outstandingMessages.Add(messageID, WS_MESSAGE_METHOD_SET_CONTROL_PROGRESS);
        }

        internal void SendSetJoystickSetCoordinates(string controlID, double x, double y)
        {
            InteractiveControl control = ControlFromControlID(controlID);
            if (control == null)
            {
                return;
            }
            var messageID = _currentmessageID++;
            StringBuilder stringBuilder = new StringBuilder();
            StringWriter stringWriter = new StringWriter(stringBuilder);
            using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_TYPE);
                jsonWriter.WriteValue(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ID);
                jsonWriter.WriteValue(messageID);
                jsonWriter.WritePropertyName(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WriteValue(WS_MESSAGE_METHOD_UPDATE_CONTROLS);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARAMETERS);
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_SCENE_ID);
                jsonWriter.WriteValue(control.SceneID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_CONTROLS);
                jsonWriter.WriteStartArray();
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_CONTROL_ID);
                jsonWriter.WriteValue(controlID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ETAG);
                jsonWriter.WriteValue(control.ETag);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_X);
                jsonWriter.WriteValue(x);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_Y);
                jsonWriter.WriteValue(y);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEnd();
                SendJsonString(stringWriter.ToString());
            }
            _outstandingMessages.Add(messageID, WS_MESSAGE_METHOD_SET_JOYSTICK_COORDINATES);
        }

        internal void SendSetControlProgress(string controlID, float progress)
        {
            InteractiveControl control = ControlFromControlID(controlID);
            if (control == null)
            {
                return;
            }
            var messageID = _currentmessageID++;
            StringBuilder stringBuilder = new StringBuilder();
            StringWriter stringWriter = new StringWriter(stringBuilder);
            using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_TYPE);
                jsonWriter.WriteValue(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ID);
                jsonWriter.WriteValue(messageID);
                jsonWriter.WritePropertyName(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WriteValue(WS_MESSAGE_METHOD_UPDATE_CONTROLS);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARAMETERS);
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_SCENE_ID);
                jsonWriter.WriteValue(control.SceneID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_CONTROLS);
                jsonWriter.WriteStartArray();
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_CONTROL_ID);
                jsonWriter.WriteValue(controlID);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ETAG);
                jsonWriter.WriteValue(control.ETag);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PROGRESS);
                jsonWriter.WriteValue(progress);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEnd();
                SendJsonString(stringWriter.ToString());
            }
            _outstandingMessages.Add(messageID, WS_MESSAGE_METHOD_SET_CONTROL_PROGRESS);
        }

        private void SendGetAllGroupsMessage()
        {
            SendMessage(WS_MESSAGE_METHOD_GET_GROUPS);
        }

        private void SendGetAllScenesMessage()
        {
            SendMessage(WS_MESSAGE_METHOD_GET_SCENES);
        }

        private void SendGetAllParticipants()
        {
            SendMessage(WS_MESSAGE_METHOD_GET_ALL_PARTICIPANTS);
        }

        private void SendMessage(string method)
        {
            uint messageID = _currentmessageID++;
            StringBuilder stringBuilder = new StringBuilder();
            StringWriter stringWriter = new StringWriter(stringBuilder);
            using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_TYPE);
                jsonWriter.WriteValue(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_ID);
                jsonWriter.WriteValue(messageID);
                jsonWriter.WritePropertyName(WS_MESSAGE_TYPE_METHOD);
                jsonWriter.WriteValue(method);
                jsonWriter.WritePropertyName(WS_MESSAGE_KEY_PARAMETERS);
                jsonWriter.WriteStartObject();
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEnd();

                try
                {
                    SendJsonString(stringWriter.ToString());
                }
                catch
                {
                    LogError("Error: Unable to send message: " + method);
                }
            }
            _outstandingMessages.Add(messageID, method);
        }

#if !WINDOWS_UWP
        private void SendJsonString(string jsonString)
#else
        private async void SendJsonString(string jsonString)
#endif
        {
            if (_websocket == null)
            {
                return;
            }

#if !WINDOWS_UWP
            if (!_websocket.IsAlive)
            {
                ConnectToWebsocket();
            }
            if (_websocket.IsAlive)
            {
                _websocket.Send(jsonString);
            }
            else
            {
                LogError("Error: Could not send message because the websocket connection was broken. Message: " + jsonString);
            }
#else
            DataWriter messageWriter = new DataWriter(_websocket.OutputStream);
            messageWriter.WriteString(jsonString);
            await messageWriter.StoreAsync();
#endif
            Log(jsonString);
        }

        List<InteractiveEventArgs> _queuedEvents = new List<InteractiveEventArgs>();
        Dictionary<uint, string> _outstandingMessages = new Dictionary<uint, string>();

#if !WINDOWS_UWP
        WebSocket _websocket;
#else
        MessageWebSocket _websocket = new MessageWebSocket();
#endif

        private string _interactiveWebSocketUrl = string.Empty;
#if !WINDOWS_UWP
        private System.Timers.Timer _checkAuthStatusTimer;
        private System.Timers.Timer _refreshShortCodeTimer;
        private System.Timers.Timer _reconnectTimer;
#else
        private ThreadPoolTimer _checkAuthStatusTimer;
        private ThreadPoolTimer _refreshShortCodeTimer;
        private ThreadPoolTimer _reconnectTimer;
        HttpClient _httpClient;
#endif
        private uint _currentmessageID = 1;
        private bool _disposed = false;
        private string _authShortCodeRequestHandle;
        private string _authToken;
        private string _oauthRefreshToken;
        private bool _initializedGroups = false;
        private bool _initializedScenes = false;
        private bool _isUnity = false;
        private bool _isUnityEditor = false;
        private bool _pendingConnectToWebSocket = false;
        private bool _shouldStartInteractive = true;
        private string _streamingAssetsPath = string.Empty;

        private List<InteractiveGroup> _groups;
        private List<InteractiveScene> _scenes;
        private List<InteractiveParticipant> _participants;
        private List<InteractiveControl> _controls;
        private List<InteractiveButtonControl> _buttons;
        private List<InteractiveJoystickControl> _joysticks;

        private const string API_BASE = "https://beam.pro/api/v1/";
        private const string WEBSOCKET_DISCOVERY_URL = API_BASE + "interactive/hosts";
        private const string API_CHECK_SHORT_CODE_AUTH_STATUS_PATH = API_BASE + "oauth/shortcode/check/";
        private const string API_GET_SHORT_CODE_PATH = API_BASE + "oauth/shortcode";
        private const string API_GET_OAUTH_TOKEN_PATH = API_BASE + "oauth/token";
        private const string INTERACTIVE_DATA_FILE_NAME = "interactivedata.json";
        private const string CONFIG_FILE_NAME = "interactiveconfig.json";
        private const int POLL_FOR_SHORT_CODE_AUTH_INTERVAL = 500; // Milliseconds
        private const int WEBSOCKET_RECONNECT_INTERVAL = 500; // Milliseconds

        // Consts
        private const string INTERACTIVE_CONFIG_FILE_NAME = "interactiveconfig.json";

        // Keys
        private const string WS_MESSAGE_KEY_ACCESS_TOKEN_FROM_FILE = "AuthToken";
        private const string WS_MESSAGE_KEY_APPID = "appid";
        private const string WS_MESSAGE_KEY_CODE = "code";
        private const string WS_MESSAGE_KEY_COOLDOWN = "cooldown";
        private const string WS_MESSAGE_KEY_CONNECTED_AT = "connectedAt";
        private const string WS_MESSAGE_KEY_CONTROLS = "controls";
        private const string WS_MESSAGE_KEY_CONTROL_ID = "controlID";
        private const string WS_MESSAGE_KEY_DISABLED = "disabled";
        private const string WS_MESSAGE_KEY_ERROR_CODE = "code";
        private const string WS_MESSAGE_KEY_ERROR_MESSAGE = "message";
        private const string WS_MESSAGE_KEY_ERROR_PATH = "path";
        private const string WS_MESSAGE_KEY_ETAG = "etag";
        private const string WS_MESSAGE_KEY_EVENT = "event";
        private const string WS_MESSAGE_KEY_EXPIRATION = "expires_in";
        private const string WS_MESSAGE_KEY_GROUP = "group";
        private const string WS_MESSAGE_KEY_GROUPS = "groups";
        private const string WS_MESSAGE_KEY_GROUP_ID = "groupID";
        private const string WS_MESSAGE_KEY_LAST_INPUT_AT = "lastInputAt";
        private const string WS_MESSAGE_KEY_HANDLE = "handle";
        private const string WS_MESSAGE_KEY_ID = "id";
        private const string WS_MESSAGE_KEY_INPUT = "input";
        private const string WS_MESSAGE_KEY_INTENSITY = "intensity";
        private const string WS_MESSAGE_KEY_ISREADY = "isReady";
        private const string WS_MESSAGE_KEY_KIND = "kind";
        private const string WS_MESSAGE_KEY_LEVEL = "level";
        private const string WS_MESSAGE_KEY_REFRESH_TOKEN = "refresh_token";
        private const string WS_MESSAGE_KEY_REFRESH_TOKEN_FROM_FILE = "RefreshToken";
        private const string WS_MESSAGE_KEY_RESULT = "result";
        private const string WS_MESSAGE_KEY_PARTICIPANT_ID = "participantID";
        private const string WS_MESSAGE_KEY_PARTICIPANTS = "participants";
        private const string WS_MESSAGE_KEY_PARAMETERS = "params";
        private const string WS_MESSAGE_KEY_PROGRESS = "progress";
        private const string WS_MESSAGE_KEY_PROJECT_VERSION_ID = "projectversionid";
        private const string WS_MESSAGE_KEY_SCENE_ID = "sceneID";
        private const string WS_MESSAGE_KEY_SCENES = "scenes";
        private const string WS_MESSAGE_KEY_SCHEME = "scheme";
        private const string WS_MESSAGE_KEY_SESSION_ID = "sessionID";
        private const string WS_MESSAGE_KEY_TEXT = "text";
        private const string WS_MESSAGE_KEY_TYPE = "type";
        private const string WS_MESSAGE_KEY_USER_ID = "userID";
        private const string WS_MESSAGE_KEY_USERNAME = "username";
        private const string WS_MESSAGE_KEY_WEBSOCKET_ACCESS_TOKEN = "access_token";
        private const string WS_MESSAGE_KEY_WEBSOCKET_ADDRESS = "address";
        private const string WS_MESSAGE_KEY_X = "x";
        private const string WS_MESSAGE_KEY_Y = "y";

        // Values
        private const string WS_MESSAGE_VALUE_CONTROL_TYPE_BUTTON = "button";
        private const string WS_MESSAGE_VALUE_DISABLED = "disabled";
        internal const string WS_MESSAGE_VALUE_DEFAULT_GROUP_ID = "default";
        internal const string WS_MESSAGE_VALUE_DEFAULT_SCENE_ID = "default";
        private const string WS_MESSAGE_VALUE_CONTROL_TYPE_JOYSTICK = "joystick";
        private const bool WS_MESSAGE_VALUE_TRUE = true;

        // Message types
        private const string WS_MESSAGE_TYPE_METHOD = "method";
        private const string WS_MESSAGE_TYPE_REPLY = "reply";

        // Methods
        private const string WS_MESSAGE_METHOD_CREATE_GROUPS = "createGroups";
        private const string WS_MESSAGE_METHOD_READY = "ready";
        private const string WS_MESSAGE_METHOD_ON_CONTROL_UPDATE = "onControlUpdate";
        private const string WS_MESSAGE_METHOD_ON_GROUP_CREATE = "onGroupCreate";
        private const string WS_MESSAGE_METHOD_ON_GROUP_UPDATE = "onGroupUpdate";
        private const string WS_MESSAGE_METHOD_ON_READY = "onReady";
        private const string WS_MESSAGE_METHOD_ON_SCENE_CREATE = "onSceneCreate";
        private const string WS_MESSAGE_METHOD_SET_COMPRESSION = "setCompression";
        private const string WS_MESSAGE_METHOD_SET_CONTROL_DISABLED = "setControlDisabled";
        private const string WS_MESSAGE_METHOD_SET_CONTROL_FIRED = "setControlFired";
        private const string WS_MESSAGE_METHOD_SET_JOYSTICK_COORDINATES = "setJoystickCoordinates";
        private const string WS_MESSAGE_METHOD_SET_JOYSTICK_INTENSITY = "setJoystickIntensity";
        private const string WS_MESSAGE_METHOD_SET_CONTROL_PROGRESS = "setControlProgress";
        private const string WS_MESSAGE_METHOD_SET_CURRENT_SCENE = "setCurrentScene";
        private const string WS_MESSAGE_METHOD_GET_ALL_PARTICIPANTS = "getAllParticipants";
        private const string WS_MESSAGE_METHOD_GET_GROUPS = "getGroups";
        private const string WS_MESSAGE_METHOD_GET_SCENES = "getScenes";
        private const string WS_MESSAGE_METHOD_GIVE_INPUT = "giveInput";
        private const string WS_MESSAGE_METHOD_PARTICIPANT_JOIN = "onParticipantJoin";
        private const string WS_MESSAGE_METHOD_PARTICIPANT_LEAVE = "onParticipantLeave";
        private const string WS_MESSAGE_METHOD_PARTICIPANT_UPDATE = "onParticipantUpdate";
        private const string WS_MESSAGE_METHOD_UPDATE_CONTROLS = "updateControls";
        private const string WS_MESSAGE_METHOD_UPDATE_GROUPS = "updateGroups";
        private const string WS_MESSAGE_METHOD_UPDATE_PARTICIPANTS = "updateParticipants";
        private const string WS_MESSAGE_METHOD_UPDATE_SCENES = "updateScenes";

        // Other message types
        private const string WS_MESSAGE_ERROR = "error";

        // Input
        private const string CONTROL_TYPE_BUTTON = "button";
        private const string CONTROL_TYPE_JOYSTICK = "joystick";
        private const string EVENT_NAME_MOUSE_DOWN = "mousedown";
        private const string EVENT_NAME_MOUSE_UP = "mouseup";

        // Message parameters
        private const string BOOLEAN_TRUE_VALUE = "true";
        private const string COMPRESSION_TYPE_GZIP = "gzip";
        private const string READY_PARAMETER_IS_READY = "isReady";

        // Errors
        private int ERROR_FAIL = 83;

        // Misc
        private const string PROTOCOL_VERSION = "2.0";
        private const int SECONDS_IN_A_MILLISECOND = 1000;

        // New data  structures
        internal static Dictionary<string, InternalButtonCountState> _buttonStates;
        internal static Dictionary<uint, Dictionary<string, InternalButtonState>> _buttonStatesByParticipant;
        internal static Dictionary<string, InternalJoystickState> _joystickStates;
        internal static Dictionary<uint, Dictionary<string, InternalJoystickState>> _joystickStatesByParticipant;

        // For MockData
        public static bool useMockData = false;

        // Ctor
        private void InitializeInternal()
        {
            UpdateInteractivityState(InteractivityState.NotInitialized);

            _buttons = new List<InteractiveButtonControl>();
            _controls = new List<InteractiveControl>();
            _groups = new List<InteractiveGroup>();
            _joysticks = new List<InteractiveJoystickControl>();
            _participants = new List<InteractiveParticipant>();
            _scenes = new List<InteractiveScene>();

            _buttonStates = new Dictionary<string, InternalButtonCountState>();
            _buttonStatesByParticipant = new Dictionary<uint, Dictionary<string, InternalButtonState>>();

#if UNITY
            _isUnityEditor = UnityEngine.Application.isEditor;
            if (_isUnityEditor)
            {
                LoggingLevel = LoggingLevel.Minimal;
            }
            else
            {
                LoggingLevel = LoggingLevel.None;
            }
#endif

            _joystickStates = new Dictionary<string, InternalJoystickState>();
            _joystickStatesByParticipant = new Dictionary<uint, Dictionary<string, InternalJoystickState>>();

#if UNITY
            _streamingAssetsPath = UnityEngine.Application.streamingAssetsPath;
#endif

            CreateStorageDirectoryIfNotExists();

#if !WINDOWS_UWP
            _checkAuthStatusTimer = new System.Timers.Timer(POLL_FOR_SHORT_CODE_AUTH_INTERVAL);
            _refreshShortCodeTimer = new System.Timers.Timer();
            _refreshShortCodeTimer.AutoReset = true;
            _refreshShortCodeTimer.Enabled = false;
            _refreshShortCodeTimer.Elapsed += RefreshShortCodeCallback;
            _reconnectTimer = new System.Timers.Timer(WEBSOCKET_RECONNECT_INTERVAL);
            _reconnectTimer.AutoReset = true;
            _reconnectTimer.Enabled = false;
            _reconnectTimer.Elapsed += ReconnectWebsocketCallback;
#else
            _httpClient = new HttpClient();
#endif

#if UNITY_EDITOR
            // Required for HTTPS traffic to succeed in the Unity editor.
            ServicePointManager.ServerCertificateValidationCallback += new RemoteCertificateValidationCallback((sender, certificate, chain, policyErrors) => { return true; });
#endif
            _isUnity = true;
        }

#if !WINDOWS_UWP
        private void RefreshShortCodeCallback(object sender, ElapsedEventArgs e)
        {
            RefreshShortCode();
        }

        private void ReconnectWebsocketCallback(object sender, ElapsedEventArgs e)
        {
            ConnectToWebsocket();
        }
#else
        private async void RefreshShortCodeCallback(ThreadPoolTimer timer)
        {
            await RefreshShortCode();
        }

        private void ReconnectWebsocketCallback(ThreadPoolTimer timer)
        {
            ConnectToWebsocket();
        }
#endif

        private void LogError(string message)
        {
            LogError(message, ERROR_FAIL);
        }

        private void LogError(string message, int code)
        {
            _queuedEvents.Add(new InteractiveEventArgs(InteractiveEventType.Error, code, message));
            Log(message, LoggingLevel.Minimal);
        }

        private void Log(string message, LoggingLevel level = LoggingLevel.Verbose)
        {
            if (LoggingLevel == LoggingLevel.None ||
                (LoggingLevel == LoggingLevel.Minimal && level == LoggingLevel.Verbose))
            {
                return;
            }
#if UNITY
            UnityEngine.Debug.Log(message);
#endif
        }

        private void ClearPreviousControlState()
        {
            List<string> _buttonStatesKeys = new List<string>(_buttonStates.Keys);
            foreach (string key in _buttonStatesKeys)
            {
                InternalButtonCountState oldButtonState = _buttonStates[key];
                InternalButtonCountState newButtonState = new InternalButtonCountState();
                newButtonState.PreviousCountOfButtonDownEvents = oldButtonState.CountOfButtonDownEvents;
                newButtonState.CountOfButtonDownEvents = oldButtonState.NextCountOfButtonDownEvents;
                newButtonState.NextCountOfButtonDownEvents = 0;

                newButtonState.PreviousCountOfButtonPressEvents = oldButtonState.CountOfButtonPressEvents;
                newButtonState.CountOfButtonPressEvents = oldButtonState.NextCountOfButtonPressEvents;
                newButtonState.NextCountOfButtonPressEvents = 0;

                newButtonState.PreviousCountOfButtonUpEvents = oldButtonState.CountOfButtonUpEvents;
                newButtonState.CountOfButtonUpEvents = oldButtonState.NextCountOfButtonUpEvents;
                newButtonState.NextCountOfButtonUpEvents = 0;

                _buttonStates[key] = newButtonState;
            }

            List<uint> _buttonStatesByParticipantKeys = new List<uint>(_buttonStatesByParticipant.Keys);
            foreach (uint key in _buttonStatesByParticipantKeys)
            {
                List<string> _buttonStatesByParticipantButtonStateKeys = new List<string>(_buttonStatesByParticipant[key].Keys);
                foreach (string controlKey in _buttonStatesByParticipantButtonStateKeys)
                {
                    InternalButtonState oldButtonCountState = _buttonStatesByParticipant[key][controlKey];
                    InternalButtonState newButtonCountState = new InternalButtonState();
                    InternalButtonCountState buttonCountState = new InternalButtonCountState();
                    buttonCountState.PreviousCountOfButtonDownEvents = oldButtonCountState.ButtonCountState.CountOfButtonDownEvents;
                    buttonCountState.CountOfButtonDownEvents = oldButtonCountState.ButtonCountState.NextCountOfButtonDownEvents;
                    buttonCountState.NextCountOfButtonDownEvents = 0;

                    buttonCountState.PreviousCountOfButtonPressEvents = oldButtonCountState.ButtonCountState.CountOfButtonPressEvents;
                    buttonCountState.CountOfButtonPressEvents = oldButtonCountState.ButtonCountState.NextCountOfButtonPressEvents;
                    buttonCountState.NextCountOfButtonPressEvents = 0;

                    buttonCountState.PreviousCountOfButtonUpEvents = oldButtonCountState.ButtonCountState.CountOfButtonUpEvents;
                    buttonCountState.CountOfButtonUpEvents = oldButtonCountState.ButtonCountState.NextCountOfButtonUpEvents;
                    buttonCountState.NextCountOfButtonUpEvents = 0;

                    newButtonCountState.ButtonCountState = buttonCountState;
                    _buttonStatesByParticipant[key][controlKey] = newButtonCountState;
                }
            }
        }

        private void UpdateInternalButtonState(InteractiveButtonEventArgs e)
        {
            // Make sure the entry exists
            uint participantId = e.Participant.UserID;
            string controlID = e.ControlID;
            Dictionary<string, InternalButtonState> buttonState;
            bool participantEntryExists = _buttonStatesByParticipant.TryGetValue(participantId, out buttonState);
            if (!participantEntryExists)
            {
                buttonState = new Dictionary<string, InternalButtonState>();
                InternalButtonState newControlButtonState = new InternalButtonState();
                newControlButtonState.IsDown = e.IsPressed;
                newControlButtonState.IsPressed = e.IsPressed;
                newControlButtonState.IsUp = !e.IsPressed;
                buttonState.Add(controlID, newControlButtonState);
                _buttonStatesByParticipant.Add(participantId, buttonState);
            }
            else
            {
                InternalButtonState controlButtonState;
                bool previousStateControlEntryExists = buttonState.TryGetValue(controlID, out controlButtonState);
                if (!previousStateControlEntryExists)
                {
                    controlButtonState = new InternalButtonState();
                    InternalButtonState newControlButtonState = new InternalButtonState();
                    newControlButtonState.IsDown = e.IsPressed;
                    newControlButtonState.IsPressed = e.IsPressed;
                    newControlButtonState.IsUp = !e.IsPressed;
                    buttonState.Add(controlID, newControlButtonState);
                }
            }

            // Populate the structure that's by participant
            bool wasPreviouslyPressed = _buttonStatesByParticipant[participantId][controlID].ButtonCountState.NextCountOfButtonPressEvents > 0;
            bool isCurrentlyPressed = e.IsPressed;
            InternalButtonState newState = _buttonStatesByParticipant[participantId][controlID];
            if (isCurrentlyPressed)
            {
                if (!wasPreviouslyPressed)
                {
                    newState.IsDown = true;
                    newState.IsPressed = true;
                    newState.IsUp = false;
                }
                else
                {
                    newState.IsDown = false;
                    newState.IsPressed = true;
                    newState.IsUp = false;
                }
            }
            else
            {
                // This means IsPressed on the event was false, so it was a mouse up event.
                newState.IsDown = false;
                newState.IsPressed = false;
                newState.IsUp = true;
            }

            // Fill in the button counts
            InternalButtonCountState ButtonCountState = newState.ButtonCountState;
            if (newState.IsDown)
            {
                ButtonCountState.NextCountOfButtonDownEvents++;
            }
            if (newState.IsPressed)
            {
                ButtonCountState.NextCountOfButtonPressEvents++;
            }
            if (newState.IsUp)
            {
                ButtonCountState.NextCountOfButtonUpEvents++;
            }
            newState.ButtonCountState = ButtonCountState;
            _buttonStatesByParticipant[participantId][controlID] = newState;

            // Populate button count state
            InternalButtonCountState existingButtonCountState;
            bool buttonStateExists = _buttonStates.TryGetValue(controlID, out existingButtonCountState);
            if (buttonStateExists)
            {
                _buttonStates[controlID] = newState.ButtonCountState;
            }
            else
            {
                _buttonStates.Add(controlID, newState.ButtonCountState);
            }
        }

        private void UpdateInternalJoystickState(InteractiveJoystickEventArgs e)
        {
            // Make sure the entry exists
            uint participantId = e.Participant.UserID;
            string controlID = e.ControlID;
            Dictionary<string, InternalJoystickState> joystickByParticipant;
            InternalJoystickState newJoystickStateByParticipant;
            bool participantEntryExists = _joystickStatesByParticipant.TryGetValue(participantId, out joystickByParticipant);
            if (!participantEntryExists)
            {
                joystickByParticipant = new Dictionary<string, InternalJoystickState>();
                newJoystickStateByParticipant = new InternalJoystickState();
                newJoystickStateByParticipant.X = e.X;
                newJoystickStateByParticipant.Y = e.Y;
                newJoystickStateByParticipant.countOfUniqueJoystickInputs = 1;
                _joystickStatesByParticipant.Add(participantId, joystickByParticipant);
            }
            else
            {
                newJoystickStateByParticipant = new InternalJoystickState();
                bool joystickByParticipantEntryExists = joystickByParticipant.TryGetValue(controlID, out newJoystickStateByParticipant);
                if (!joystickByParticipantEntryExists)
                {
                    newJoystickStateByParticipant.X = e.X;
                    newJoystickStateByParticipant.Y = e.Y;
                    newJoystickStateByParticipant.countOfUniqueJoystickInputs = 1;
                    joystickByParticipant.Add(controlID, newJoystickStateByParticipant);
                }
                int countOfUniqueJoystickByParticipantInputs = newJoystickStateByParticipant.countOfUniqueJoystickInputs;
                // We always give the average of the joystick so that there is input smoothing.
                newJoystickStateByParticipant.X =
                    (newJoystickStateByParticipant.X * (countOfUniqueJoystickByParticipantInputs - 1) / (countOfUniqueJoystickByParticipantInputs)) +
                    (e.X * (1 / countOfUniqueJoystickByParticipantInputs));
                newJoystickStateByParticipant.Y =
                    (newJoystickStateByParticipant.Y * (countOfUniqueJoystickByParticipantInputs - 1) / (countOfUniqueJoystickByParticipantInputs)) +
                    (e.Y * (1 / countOfUniqueJoystickByParticipantInputs));
            }
            _joystickStatesByParticipant[e.Participant.UserID][e.ControlID] = newJoystickStateByParticipant;

            // Update the joystick state
            InternalJoystickState newJoystickState;
            bool joystickEntryExists = joystickByParticipant.TryGetValue(controlID, out newJoystickState);
            if (!joystickEntryExists)
            {
                newJoystickState.X = e.X;
                newJoystickState.Y = e.Y;
                newJoystickState.countOfUniqueJoystickInputs = 1;
                joystickByParticipant.Add(controlID, newJoystickState);
            }
            newJoystickState.countOfUniqueJoystickInputs++;
            int countOfUniqueJoystickInputs = newJoystickState.countOfUniqueJoystickInputs;
            // We always give the average of the joystick so that there is input smoothing.
            newJoystickState.X =
                (newJoystickState.X * (countOfUniqueJoystickInputs - 1) / (countOfUniqueJoystickInputs)) +
                (e.X * (1 / countOfUniqueJoystickInputs));
            newJoystickState.Y =
                (newJoystickState.Y * (countOfUniqueJoystickInputs - 1) / (countOfUniqueJoystickInputs)) +
                (e.Y * (1 / countOfUniqueJoystickInputs));
            _joystickStates[e.ControlID] = newJoystickState;
        }
    }

    internal struct InternalButtonCountState
    {
        internal uint PreviousCountOfButtonDownEvents;
        internal uint PreviousCountOfButtonPressEvents;
        internal uint PreviousCountOfButtonUpEvents;

        internal uint CountOfButtonDownEvents;
        internal uint CountOfButtonPressEvents;
        internal uint CountOfButtonUpEvents;

        internal uint NextCountOfButtonDownEvents;
        internal uint NextCountOfButtonPressEvents;
        internal uint NextCountOfButtonUpEvents;
    }

    internal struct InternalButtonState
    {
        internal bool IsDown;
        internal bool IsPressed;
        internal bool IsUp;
        internal InternalButtonCountState ButtonCountState;
    }

    internal struct InternalJoystickState
    {
        internal double X;
        internal double Y;
        internal int countOfUniqueJoystickInputs;
    }
}