﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using Fleck2.Interfaces;
using Jayrock.Json;
using Jayrock.Json.Conversion;
using Jayrock.JsonRpc;
using KeePassLib.Utility;
using KeePassRPC.Forms;
using KeePassRPC.JsonRpc;
using KeePassRPC.Models.DataExchange;

namespace KeePassRPC
{
    /// <summary>
    /// Represents a client that has connected to this RPC server.
    /// </summary>
    public class KeePassRPCClientConnection
    {
        // wanted to use uint really but that seems to break Jayrock JSON-RPC - presumably because there is no such concept in JavaScript
        static private int _protocolVersion;

        private static int ProtocolVersion { get {
            if (_protocolVersion == 0)
            {
                _protocolVersion = BitConverter.ToInt32(new byte[] {
                    (byte)KeePassRPCExt.PluginVersion.Build,
                    (byte)KeePassRPCExt.PluginVersion.Minor,
                    (byte)KeePassRPCExt.PluginVersion.Major,0},0);
            }
            return _protocolVersion;
        } }
        
        private static string[] featuresOffered = {

            // Full feature set as of KeeFox 1.6
            "KPRPC_FEATURE_VERSION_1_6",

            // Allow clients without the name KeeFox to connect
            "KPRPC_GENERAL_CLIENTS",

            // Renamed KeeFox to Kee
            "KPRPC_FEATURE_KEE_BRAND",

            // GetAllEntries or GetAllChildEntries can be used to 
            // include results even if they have no URL
            "KPRPC_ENTRIES_WITH_NO_URL",

            // Form fields with no configured name or ID will output an empty value
            // Before this feature, default name and IDs were used ("username" and "password")
            "KPRPC_FIELD_DEFAULT_NAME_AND_ID_EMPTY",

            // OpenAndFocusDatabase can focus KeePass with a database, opening it first if required
            "KPRPC_OPEN_AND_FOCUS_DATABASE",

            // Allow replacement of all URLs during entry update
            "KPRPC_FEATURE_ENTRY_URL_REPLACEMENT",

            // Contains critical security fixes
            "KPRPC_SECURITY_FIX_20200729",

            // Can send new DTO format
            "KPRPC_FEATURE_DTO_V2"

            // in the rare event that we want to check for the absense of a feature
            // we would add a feature flag along the lines of "KPRPC_FEATURE_REMOVED_INCOMPATIBLE_THING_X"

        };

        private static string[] featuresRequired = {

            // Full feature set as of KeeFox 1.6
            "KPRPC_FEATURE_VERSION_1_6",

            // Trivial example showing how we've required a new client feature
            "KPRPC_FEATURE_WARN_USER_WHEN_FEATURE_MISSING"

        };

        /// <summary>
        /// The ID of the next signal we'll send to the client
        /// </summary>
        private int _currentCallBackId;
        private bool _authorised;
        private IWebSocketConnection _webSocketConnection;
        private SRP _srp;
        private KeyChallengeResponse _kcp;
        private int securityLevel;
        private int securityLevelClientMinimum;
        private string userName;
        private string[] _clientFeatures;

        // Read-only username is accessible to anyone but only once the connection has been confirmed
        public string UserName { get
        {
            if (Authorised) return userName;
            return "";
        } }

        private KeyChallengeResponse Kcp
        {
            get { return _kcp; }
            set { _kcp = value; }
        }

        private AuthForm _authForm;
        private KeePassRPCExt KPRPC;
        
        /// <summary>
        /// The underlying web socket connection that links us to this client.
        /// </summary>
        public IWebSocketConnection WebSocketConnection
        {
            get { return _webSocketConnection; }
            private set { _webSocketConnection = value; }
        }
        
        /// <summary>
        /// Whether this client has successfully authenticated to the
        /// server and been authorised to communicate with KeePass
        /// </summary>
        public bool Authorised
        {
            get { return _authorised; }
            set { _authorised = value; }
        }
        
        /// <summary>
        /// The features this client claims to support
        /// </summary>
        public string[] ClientFeatures
        {
            get { return _clientFeatures; }
        }


        private long KeyExpirySeconds
        {
            get
            {
                // read from config file
                return KPRPC._host.CustomConfig.GetLong("KeePassRPC.AuthorisationExpiryTime", 31536000);
            }
        }

        /// <summary>
        /// The secret key used to encrypt messages
        /// </summary>
        private KeyContainerClass KeyContainer
        {
            get {
                if (_keyContainer == null)
                {
                    // if we're already authorised to communicate but do not have the key yet, we know it's waiting for us in the recently authenticated SRP object
                    if (Authorised)
                    {
                        _keyContainer = new KeyContainerClass(_srp.Key, DateTime.UtcNow.AddSeconds(KeyExpirySeconds), userName, clientName);
                    }
                        // otherwise we know that the key is going to be stored according to spec (if not we'll return a null key to trigger a fresh SRP auth process)
                    else
                    {
                        byte[] serialisedKeyContainer = null;

                        // check security level and find key in appropriate place
                        if (securityLevel == 1)
                        {
                            // read from config file
                            string serialisedKeyContainerString = KPRPC._host.CustomConfig.GetString("KeePassRPC.Key." + userName, "");
                            if (string.IsNullOrEmpty(serialisedKeyContainerString))
                                return null;
                            serialisedKeyContainer = Convert.FromBase64String(serialisedKeyContainerString);
                        }
                        else if (securityLevel == 2)
                        {
                            // read from encrypted config file
                            string secret = KPRPC._host.CustomConfig.GetString("KeePassRPC.Key." + userName, "");
                            if (string.IsNullOrEmpty(secret))
                                return null;
                            try
                            {
                                byte[] keyBytes = ProtectedData.Unprotect(
                                Convert.FromBase64String(secret),
                                new byte[] { 172, 218, 37, 36, 15 },
                                DataProtectionScope.CurrentUser);
                                serialisedKeyContainer = keyBytes;
                            }
                            catch (Exception)
                            {
                                // This can happen if user changes from medium security to low security
                                // and maybe other operating system / .NET failures
                                return null;
                            }
                        }
                        else
                            return null;

                        if (serialisedKeyContainer == null)
                            return null;
                        try
                        {
                            XmlSerializer mySerializer = new XmlSerializer(typeof(KeyContainerClass));
                            using (MemoryStream ms = new MemoryStream(serialisedKeyContainer))
                            {
                                KeyContainerClass keyContainer = (KeyContainerClass) mySerializer.Deserialize(ms);
                                    
                                // A serialised key equal to sha256('0') suggests previous successful exploit of CVE-2020-16271
                                if (keyContainer == null || 
                                    keyContainer.Key == "5feceb66ffc86f38d952786c6d696c79c2dbc239dd4e91b46729d73a27fb57e9")
                                {
                                    Utils.ShowMonoSafeMessageBox(@"Your KeePass instance may have previously been exploited by a malicious attacker.

The passwords contained within any databases that were open before this point may have been exposed so you should change them.

See https://forum.kee.pm/t/3143/ for more information.",
                                        "WARNING!",
                                        MessageBoxButtons.OK, 
                                        MessageBoxIcon.Warning);
                                    return null;
                                }

                                _keyContainer = keyContainer;
                            }
                        }
                        catch (Exception)
                        {
                            return null;
                        }
                    }
                }
                return _keyContainer;
            }
            set
            {
                _keyContainer = value;

                KeyContainerClass kc = new KeyContainerClass(_srp.Key, DateTime.UtcNow.AddSeconds(KeyExpirySeconds),
                    userName, clientName);

                XmlSerializer mySerializer = new
                    XmlSerializer(typeof(KeyContainerClass));
                byte[] serialisedKeyContainer;
                using (MemoryStream myWriter = new MemoryStream())
                {
                    mySerializer.Serialize(myWriter, kc);
                    serialisedKeyContainer = myWriter.ToArray();
                }

                // We probably want to store the key somewhere that will persist beyond an application restart
            if (securityLevel == 1)
                {
                    // Store unencrypted in config file
                    KPRPC._host.CustomConfig.SetString("KeePassRPC.Key." + userName, Convert.ToBase64String(serialisedKeyContainer));
                    KPRPC._host.MainWindow.Invoke((MethodInvoker)delegate { KPRPC._host.MainWindow.SaveConfig(); });
                }
                else if (securityLevel == 2)
                {
                    try
                    {
                        // Encrypt the data using DataProtectionScope.CurrentUser. The result can be decrypted 
                        //  only by the same current user. 

                        byte[] secret = ProtectedData.Protect(
                            serialisedKeyContainer,
                            new byte[] { 172, 218, 37, 36, 15 },
                            DataProtectionScope.CurrentUser);

                        KPRPC._host.CustomConfig.SetString("KeePassRPC.Key." + userName, Convert.ToBase64String(secret));
                        KPRPC._host.MainWindow.Invoke((MethodInvoker)delegate { KPRPC._host.MainWindow.SaveConfig(); });
                    }
                    catch (CryptographicException e)
                    {
                        if (KPRPC.logger != null) KPRPC.logger.WriteLine("Could not store KeePassRPC's secret key so you will have to re-authenticate clients such as Kee in your web browser. The following exception caused this problem: " + e);
                    }
                }
                // else we don't persist the key anywhere - no security implications
                // of this fallback behaviour but it will be annoying for the user
            }
        }

        private KeyContainerClass _keyContainer;
        private string clientName;
        
        public KeePassRPCClientConnection(IWebSocketConnection connection, bool isAuthorised, KeePassRPCExt kprpc)
        {
            WebSocketConnection = connection;
            Authorised = isAuthorised;

            //TODO2: Can we lazy load these since some sessions will require only one of these authentication mechanisms?
            _srp = new SRP();
            Kcp = new KeyChallengeResponse(ProtocolVersion, featuresOffered);

            // Load from config, default to medium security if user has not yet requested anything different
            securityLevel = (int)kprpc._host.CustomConfig.GetLong("KeePassRPC.SecurityLevel", 2);
            securityLevelClientMinimum = (int)kprpc._host.CustomConfig.GetLong("KeePassRPC.SecurityLevelClientMinimum", 2);
            KPRPC = kprpc;
        }

        /// <summary>
        /// Sends the specified signal to the client.
        /// </summary>
        /// <param name="signal">The signal.</param>
        public void Signal(Signal signal, string methodName)
        {
            // User may not have authorised the connection we are trying to signal
            if (KeyContainer == null) return;

            try
            {
                JsonObject call = new JsonObject();
                call["id"] = ++_currentCallBackId;
                call["method"] = methodName;
                call["params"] = new[] { (int)signal };

                StringBuilder sb = new StringBuilder();
                JsonConvert.Export(call, sb);
                KPRPCMessage data2client = new KPRPCMessage();
                data2client.protocol = "jsonrpc";
                data2client.version = ProtocolVersion;
                data2client.jsonrpc = Encrypt(sb.ToString());

                // If there was a problem encrypting our message, just abort - the
                // client won't be able to do anything useful with an error message
                if (data2client.jsonrpc == null)
                {
                    if (KPRPC.logger != null) KPRPC.logger.WriteLine("Encryption error when trying to send signal: " + signal);
                    return;
                }

                // Signalling through the websocket needs to be processed on a different thread becuase handling the incoming messages results in a lock on the list of known connections (which also happens before this Signal function is called) so we want to process this as quickly as possible and avoid deadlocks.
                
                // Respond to each message on a different thread
                ThreadStart work = delegate
                {
                    WebSocketConnection.Send(JsonConvert.ExportToString(data2client));
                };
                Thread messageHandler = new Thread(work);
                messageHandler.Name = "signalDispatcher";
                messageHandler.Start();
            }
            catch (IOException)
            {
                // Sometimes a connection is unexpectedly closed e.g. by Firefox
                // or (more likely) dodgy security "protection". From one year's
                // worth of bug reports (35) 100% of unexpected application
                // exceptions were IOExceptions.
                //
                // We will now ignore this type of exception and allow the client to
                // re-establish the link to KeePass as part of its regular polling loop.
                //
                // The requested KPRPC signal will never be recieved by the client
                // but this should be OK in practice becuase the client will 
                // re-establish the relevant state information as soon as it reconnects.
                //
                // BUT: the exception to this rule is when the client fails to receive the
                // "shutdown" signal - it then gets itself in an inconsistent state
                // and has no opportunity to recover until KeePass is running again.
            }
            catch (Exception ex)
            {
                Utils.ShowMonoSafeMessageBox("ERROR! Please click on this box, press CTRL-C on your keyboard and paste into a new post on the Kee forum (https://forum.kee.pm). Doing this will help other people to use Kee without any unexpected error messages like this. Please briefly describe what you were doing when the problem occurred, which version of Kee, KeePass and web browser you use and what other security software you run on your machine. Thanks! Technical detail follows: " + ex);
            }
        }

        public void ReceiveMessage(string message, KeePassRPCService service)
        {
            // Inspect incoming message
            KPRPCMessage kprpcm;

            try
            {
                kprpcm = (KPRPCMessage)JsonConvert.Import(typeof(KPRPCMessage), message);
            }
            catch (Exception )
            {
                kprpcm = null;
            }

            if (kprpcm == null)
            {
                KPRPCMessage data2client = new KPRPCMessage();
                data2client.protocol = "error";
                data2client.srp = new SRPParams();
                data2client.version = ProtocolVersion;

                data2client.error = new Error(ErrorCode.INVALID_MESSAGE, new[] { "Contents can't be interpreted as an SRPEncapsulatedMessage" });

                AbortWithMessageToClient(data2client);
                return;
            }
            
            // store supplied features until connection reset so we don't have to inject
            // them into every stage of the handshake but can still cleanly handle old 
            // versions of clients that don't send a list of features at any time.
            // Changing features mid-connection seems odd and might be an attack vector
            // so we don't allow that.
            if (kprpcm.features != null && _clientFeatures == null)
            {
                _clientFeatures = kprpcm.features;
            }

            // Assume that a matching client and server protocol version mean that the client supports the required features
            if (kprpcm.version != ProtocolVersion)
            {
                if (!ClientSupportsRequiredFeatures())
                {
                    RejectClientVersion(kprpcm);
                    return;
                }
            }
            
            switch (kprpcm.protocol)
            {
                case "setup": KPRPCReceiveSetup(kprpcm); break;
                case "jsonrpc": KPRPCReceiveJSONRPC(kprpcm.jsonrpc, service); break;
                default: KPRPCMessage data2client = new KPRPCMessage();
                    data2client.protocol = "error";
                    data2client.srp = new SRPParams();
                    data2client.version = ProtocolVersion;

                    data2client.error = new Error(ErrorCode.UNRECOGNISED_PROTOCOL, new[] { "Use setup or jsonrpc" });

                    AbortWithMessageToClient(data2client);
                    return;
            }

        }

        private bool ClientSupportsRequiredFeatures()
        {
            return _clientFeatures != null && !featuresRequired.Except(_clientFeatures).Any();
        }

        private void RejectClientVersion(KPRPCMessage kprpcm)
        {
            KPRPCMessage data2client = new KPRPCMessage();
            data2client.protocol = "error";
            data2client.srp = new SRPParams();
            data2client.version = ProtocolVersion;

            // From 1.7 onwards, the client can never be too new but can be too low if we find that it is missing essential features
            data2client.error = new Error(ErrorCode.VERSION_CLIENT_TOO_LOW, new[] { ProtocolVersion.ToString() });
            AbortWithMessageToClient(data2client);
        }

        private void AbortWithMessageToClient(KPRPCMessage data2client)
        {
            Authorised = false;
            _clientFeatures = null;
            string response = JsonConvert.ExportToString(data2client);
            WebSocketConnection.Send(response);
        }

        private void KPRPCReceiveSetup (KPRPCMessage kprpcm) {

            if (Authorised)
            {
                KPRPCMessage data2client = new KPRPCMessage();
                data2client.protocol = "setup";
                data2client.srp = new SRPParams();
                data2client.version = ProtocolVersion;

                data2client.error = new Error(ErrorCode.AUTH_RESTART, new[] { "Already authorised" });

                AbortWithMessageToClient(data2client);
                return;
            }

            if (kprpcm.srp != null)
            {
                KPRPCMessage data2client = new KPRPCMessage();
                data2client.protocol = "setup";
                data2client.version = ProtocolVersion;

                int clientSecurityLevel = kprpcm.srp.securityLevel;

                if (clientSecurityLevel < securityLevelClientMinimum)
                {
                    data2client.error = new Error(ErrorCode.AUTH_CLIENT_SECURITY_LEVEL_TOO_LOW, new[] { securityLevelClientMinimum.ToString() });
                    /* TODO1.3: need to disconnect/delete/reset this connection once we've decided we are not interested in letting the client connect. Maybe 
                     * tie in to finding a way to abort if user clicks a "cancel" button on the auth form.
                     */
                    WebSocketConnection.Send(JsonConvert.ExportToString(data2client));
                }
                else
                {
                    switch (kprpcm.srp.stage)
                    {
                        case "identifyToServer": WebSocketConnection.Send(SRPIdentifyToServer(kprpcm)); break;
                        case "proofToServer": WebSocketConnection.Send(SRPProofToServer(kprpcm)); break;
                        default: return;
                    }
                }
            }
            else
            {
                KPRPCMessage data2client = new KPRPCMessage();
                data2client.protocol = "setup";
                data2client.version = ProtocolVersion;

                int clientSecurityLevel = kprpcm.key.securityLevel;

                if (clientSecurityLevel < securityLevelClientMinimum)
                {
                    data2client.error = new Error(ErrorCode.AUTH_CLIENT_SECURITY_LEVEL_TOO_LOW, new[] { securityLevelClientMinimum.ToString() });
                    /* TODO1.3: need to disconnect/delete/reset this connection once we've decided we are not interested in letting the client connect. Maybe 
                     * tie in to finding a way to abort if user clicks a "cancel" button on the auth form.
                     */
                    WebSocketConnection.Send(JsonConvert.ExportToString(data2client));
                }
                else
                {
                    if (!string.IsNullOrEmpty(kprpcm.key.username))
                    {
                        // confirm username
                        userName = kprpcm.key.username;
                        KeyContainerClass kc = KeyContainer;

                        if (kc == null)
                        {
                            userName = null;
                            data2client.error = new Error(ErrorCode.AUTH_FAILED, new[] { "Stored key not found - Caused by changed Firefox profile or KeePass instance; changed OS user credentials; or KeePass config file may be corrupt" });
                            /* TODO1.3: need to disconnect/delete/reset this connection once we've decided we are not interested in letting the client connect. Maybe 
                             * tie in to finding a way to abort if user clicks a "cancel" button on the auth form.
                             */
                            WebSocketConnection.Send(JsonConvert.ExportToString(data2client));
                            return;
                        } 
                        if (kc.Username != userName)
                        {
                            userName = null;
                            data2client.error = new Error(ErrorCode.AUTH_FAILED, new[] { "Username mismatch - KeePass config file is probably corrupt" });
                            /* TODO1.3: need to disconnect/delete/reset this connection once we've decided we are not interested in letting the client connect. Maybe 
                             * tie in to finding a way to abort if user clicks a "cancel" button on the auth form.
                             */
                            WebSocketConnection.Send(JsonConvert.ExportToString(data2client));
                            return;
                        }
                        if (kc.AuthExpires < DateTime.UtcNow)
                        {
                            userName = null;
                            data2client.error = new Error(ErrorCode.AUTH_EXPIRED);
                            /* TODO1.3: need to disconnect/delete/reset this connection once we've decided we are not interested in letting the client connect. Maybe 
                             * tie in to finding a way to abort if user clicks a "cancel" button on the auth form.
                             */
                            WebSocketConnection.Send(JsonConvert.ExportToString(data2client));
                            return;
                        }

                        WebSocketConnection.Send(Kcp.KeyChallengeResponse1(userName, securityLevel));
                    }
                    else if (!string.IsNullOrEmpty(kprpcm.key.cc) && !string.IsNullOrEmpty(kprpcm.key.cr))
                    {
                        bool authorised = false;
                        WebSocketConnection.Send(Kcp.KeyChallengeResponse2(kprpcm.key.cc, kprpcm.key.cr, KeyContainer, securityLevel, out authorised));
                        Authorised = authorised;
                        if (authorised)
                        {
                            // We assume the user has manually verified the client name as part of the initial SRP setup so it's fairly safe to use it to determine the type of client connection to which we want to promote our null connection
                            KPRPC.PromoteGeneralRPCClient(this, KeyContainer.ClientName);
                        }
                    }
                }
            }

  	    }

        private string SRPIdentifyToServer (KPRPCMessage srpem)
        {
            SRPParams srp = srpem.srp;
            Error error;
            KPRPCMessage data2client = new KPRPCMessage();
            data2client.protocol = "setup";
            data2client.srp = new SRPParams();
            data2client.srp.stage = "identifyToClient";
            data2client.version = ProtocolVersion;
            data2client.features = featuresOffered;

            // Generate a new random password
            // SRP isn't very susceptible to brute force attacks but we get 32 bits worth of randomness just in case
            byte[] password = Utils.GetRandomBytes(4);
            string plainTextPassword = Utils.GetTypeablePassword(password);

            // caclulate the hash of our randomly generated password
            _srp.CalculatePasswordHash(plainTextPassword);


            if (string.IsNullOrEmpty(srp.I))
            {
                data2client.error = new Error(ErrorCode.AUTH_MISSING_PARAM, new[] { "I" });
            }
            else if (string.IsNullOrEmpty(srp.A))
            {
                data2client.error = new Error(ErrorCode.AUTH_MISSING_PARAM, new[] { "A" });
            }
            else
            {

                // Init relevant SRP protocol variables
                _srp.Setup();

                // Begin the SRP handshake
                error = _srp.Handshake(srp.I, srp.A);

                if (error.code > 0)
                    data2client.error = error;
                else
                {
                    // store the username and client name for future reference
                    userName = _srp.I;
                    clientName = srpem.clientDisplayName;

                    data2client.srp.s = _srp.s;
                    data2client.srp.B = _srp.Bstr;

                    data2client.srp.securityLevel = securityLevel;

                    //pass the params through to the main kprpcext thread via begininvoke - that function will then create and show the form as a modal dialog
                    string secLevel = "low";
                    if (srp.securityLevel == 2)
                        secLevel = "medium";
                    else if (srp.securityLevel == 3)
                        secLevel = "high";
                    KPRPC.InvokeMainThread (new ShowAuthDialogDelegate(ShowAuthDialog), secLevel, srpem.clientDisplayName, srpem.clientDisplayDescription, plainTextPassword);
                }
            }
	    	    
            return JsonConvert.ExportToString(data2client);
  	    }

        private delegate void ShowAuthDialogDelegate(string securityLevel, string name, string description, string password);

        private delegate void HideAuthDialogDelegate();


        private void ShowAuthDialog(string securityLevel, string name, string description, string password)
        {
            if (_authForm != null)
                _authForm.Hide();
            _authForm = new AuthForm(this, securityLevel, name, description, password);
            _authForm.Show();
        }

        private void HideAuthDialog()
        {
            if (_authForm != null)
                _authForm.Hide();
        }

        public void ShuttingDown()
        {
            // Hide the auth dialog as long as we're not trying to shut down the main thread at the same time
            // (and as long as this isn't a v<1.2 connection)
            if (KPRPC != null && !KPRPC.terminating)
                KPRPC.InvokeMainThread(new HideAuthDialogDelegate(HideAuthDialog));
        }

        private string SRPProofToServer(KPRPCMessage srpem)
        {
            SRPParams srp = srpem.srp;

            KPRPCMessage data2client = new KPRPCMessage();
            data2client.protocol = "setup";
            data2client.srp = new SRPParams();
            data2client.srp.stage = "proofToClient";
            data2client.version = ProtocolVersion;

            if (string.IsNullOrEmpty(srp.M))
            {
                data2client.error = new Error(ErrorCode.AUTH_MISSING_PARAM, new[] { "M" });
            }
            else
            {
                _srp.Authenticate(srp.M);

                if (!_srp.Authenticated)
                    data2client.error = new Error(ErrorCode.AUTH_FAILED, new[] { "Keys do not match" });
                else
                {
                    data2client.srp.M2 = _srp.M2;
                    data2client.srp.securityLevel = securityLevel;
                    KeyContainer = new KeyContainerClass(_srp.Key,DateTime.UtcNow.AddSeconds(KeyExpirySeconds),userName,clientName);
                    Authorised = true;
                    // We assume the user has checked the client name as part of the initial SRP setup so it's fairly safe to use it to determine the type of client connection to which we want to promote our null connection
                    KPRPC.PromoteGeneralRPCClient(this, clientName);
                    KPRPC.InvokeMainThread(new HideAuthDialogDelegate(HideAuthDialog));

                    // If we've never shown the user the welcome screen and have never
                    // known a Kee add-on from the previous KPRPC protocol, show it now
                    bool welcomeDisplayed = KPRPC._host.CustomConfig.GetBool("KeePassRPC.KeeFoxWelcomeDisplayed",false);
                    if (!welcomeDisplayed
                        && string.IsNullOrEmpty(KPRPC._host.CustomConfig.GetString("KeePassRPC.knownClients.KeeFox Firefox add-on")))
                        KPRPC.InvokeMainThread(new KeePassRPCExt.WelcomeKeeUserDelegate(KPRPC.WelcomeKeeUser));
                    if (!welcomeDisplayed)
                        KPRPC._host.CustomConfig.SetBool("KeePassRPC.KeeFoxWelcomeDisplayed",true);
                }
            }

            return JsonConvert.ExportToString(data2client);
  	    }

        private void KPRPCReceiveJSONRPC(JSONRPCContainer jsonrpcEncrypted, KeePassRPCService service)
        {
            string jsonrpc = Decrypt(jsonrpcEncrypted);
            StringBuilder sb = new StringBuilder();
            string output;

            JsonRpcDispatcherFactory.Current = s => new KprpcJsonRpcDispatcher(s);
            JsonRpcDispatcher dispatcher = JsonRpcDispatcherFactory.CreateDispatcher(service);
            (dispatcher as KprpcJsonRpcDispatcher).ClientMetadata = new ClientMetadata
            {
                Features = ClientFeatures
            };
            
            using (StringReader request = new StringReader(jsonrpc))
            using (StringWriter response = new StringWriter(sb))
            {
                dispatcher.Process(request, response, Authorised);
                output = sb.ToString();
            }

            KPRPCMessage data2client = new KPRPCMessage();
            data2client.protocol = "jsonrpc";
            data2client.version = ProtocolVersion;
            data2client.jsonrpc = Encrypt(output);

            // If there was a problem encrypting our message, respond to the
            // client with a non-encrypted error message
            if (data2client.jsonrpc == null)
            {
                data2client = new KPRPCMessage();
                data2client.protocol = "error";
                data2client.version = ProtocolVersion;
                data2client.error = new Error(ErrorCode.AUTH_RESTART, new[] { "Encryption error" });
                Authorised = false;
                if (KPRPC.logger != null) KPRPC.logger.WriteLine("Encryption error when trying to reply to client message");
            }
            _webSocketConnection.Send(JsonConvert.ExportToString(data2client));
            
        }

        public JSONRPCContainer Encrypt(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
                return null;

            KeyContainerClass kc = KeyContainer;

            byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

            // Encrypt the client's message
            using (SHA1 sha = new SHA1CryptoServiceProvider())
            using (RijndaelManaged myRijndael = new RijndaelManaged())
            {
                myRijndael.GenerateIV();
                myRijndael.Key = MemUtil.HexStringToByteArray(kc.Key);
                ICryptoTransform encryptor = myRijndael.CreateEncryptor();
                byte[] encrypted;
                using (MemoryStream msEncrypt = new MemoryStream(100))
                {
                    using (CryptoStream cryptoStream = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    {
                        try
                        {
                            cryptoStream.Write(plaintextBytes, 0, plaintextBytes.Length);
                        }
                        catch (ArgumentException)
                        {
                            //The sum of the count and offset parameters is longer than the length of the buffer.
                            return null;
                        }
                        catch (NotSupportedException)
                        {
                            // Underlying stream does not support writing (not sure how this could happen)
                            return null;
                        }

                        try
                        {
                            cryptoStream.FlushFinalBlock();
                        }
                        catch (NotSupportedException)
                        {
                            // 	The current stream is not writable. -or- The final block has already been transformed. 
                            return null;
                        }
                        catch (CryptographicException)
                        {
                            // The key is corrupt which can cause invalid padding to the stream. 
                            return null;
                        }

                        encrypted = msEncrypt.ToArray();
                    }
                }


                // Get the raw bytes that are used to calculate the HMAC

                byte[] HmacKey = sha.ComputeHash(myRijndael.Key);
                byte[] ourHmacSourceBytes = new byte[HmacKey.Length + encrypted.Length + myRijndael.IV.Length];

                // These calls can throw a variety of different exceptions but
                // I can't see why they would so we will not try to differentiate the cause of them
                try
                {
                    //TODO2: HMAC calculations might be stengthened against attacks on SHA 
                    // and/or gain improved performance through use of algorithms like AES-CMAC or HKDF

                    Array.Copy(HmacKey, ourHmacSourceBytes, HmacKey.Length);
                    Array.Copy(encrypted, 0, ourHmacSourceBytes, HmacKey.Length, encrypted.Length);
                    Array.Copy(myRijndael.IV, 0, ourHmacSourceBytes, HmacKey.Length + encrypted.Length, myRijndael.IV.Length);

                    // Calculate the HMAC
                    byte[] ourHmac = sha.ComputeHash(ourHmacSourceBytes);

                    // Package the data ready for transmission
                    JSONRPCContainer cont = new JSONRPCContainer();
                    cont.iv = Convert.ToBase64String(myRijndael.IV);
                    cont.message = Convert.ToBase64String(encrypted);
                    cont.hmac = Convert.ToBase64String(ourHmac);

                    return cont;
                }
                catch (ArgumentNullException)
                {
                    return null;
                }
                catch (RankException)
                {
                    return null;
                }
                catch (ArrayTypeMismatchException)
                {
                    return null;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
                catch (ArgumentException)
                {
                    return null;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
            }
        }

        public string Decrypt(JSONRPCContainer jsonrpcEncrypted)
        {
            if (string.IsNullOrEmpty(jsonrpcEncrypted.message)
                || string.IsNullOrEmpty(jsonrpcEncrypted.iv)
                || string.IsNullOrEmpty(jsonrpcEncrypted.hmac))
                return null;

            KeyContainerClass kc = KeyContainer;

                byte[] rawKeyBytes;
                byte[] keyBytes;
                byte[] messageBytes;
                byte[] IVBytes;

            using (SHA1 sha = new SHA1CryptoServiceProvider())
            {
                // Get the raw bytes that are used to calculate the HMAC
                try
                {
                    rawKeyBytes = MemUtil.HexStringToByteArray(kc.Key);
                    keyBytes = sha.ComputeHash(rawKeyBytes);
                    messageBytes = Convert.FromBase64String(jsonrpcEncrypted.message);
                    IVBytes = Convert.FromBase64String(jsonrpcEncrypted.iv);
                }
                catch (FormatException)
                {
                    // Should only happen if there is a fault with the client end
                    // of the protocol or if an attacker tries to inject invalid data
                    return null;
                }
                catch (ArgumentNullException)
                {
                    // kc.Key must = null
                    return null;
                }

            // These calls can throw a variety of different exceptions but
            // I can't see why they would so we will not try to differentiate the cause of them
            try
                {
                    byte[] ourHmacSourceBytes = new byte[keyBytes.Length + messageBytes.Length + IVBytes.Length];
                    Array.Copy(keyBytes, ourHmacSourceBytes, keyBytes.Length);
                    Array.Copy(messageBytes, 0, ourHmacSourceBytes, keyBytes.Length, messageBytes.Length);
                    Array.Copy(IVBytes, 0, ourHmacSourceBytes, keyBytes.Length + messageBytes.Length, IVBytes.Length);

                    // Calculate the HMAC
                    byte[] ourHmac = sha.ComputeHash(ourHmacSourceBytes);

                    // Check our HMAC against the one supplied by the client
                    if (Convert.ToBase64String(ourHmac) != jsonrpcEncrypted.hmac)
                    {
                        //TODO2: If we ever want/need to include some DOS protection we
                        // could use this error condition to throttle requests from badly behaved clients
                        if (KPRPC.logger != null) KPRPC.logger.WriteLine("HMAC did not match");
                        return null;
                    }
                }
                catch (ArgumentNullException)
                {
                    return null;
                }
                catch (RankException)
                {
                    return null;
                }
                catch (ArrayTypeMismatchException)
                {
                    return null;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
                catch (ArgumentException)
                {
                    return null;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
            }

            // Decrypt the client's message
            using (RijndaelManaged myRijndael = new RijndaelManaged())
            {
                ICryptoTransform decryptor = myRijndael.CreateDecryptor(rawKeyBytes, IVBytes);
                using (MemoryStream msDecrypt = new MemoryStream())
                using (CryptoStream cryptoStream = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Write))
                {
                    try
                    {
                        cryptoStream.Write(messageBytes, 0, messageBytes.Length);
                    }
                    catch (ArgumentException)
                    {
                        //The sum of the count and offset parameters is longer than the length of the buffer.
                        return null;
                    }
                    catch (NotSupportedException)
                    {
                        // Underlying stream does not support writing (not sure how this could happen)
                        return null;
                    }

                    try
                    {
                        cryptoStream.FlushFinalBlock();
                    }
                    catch (NotSupportedException)
                    {
                        // 	The current stream is not writable. -or- The final block has already been transformed. 
                        return null;
                    }
                    catch (CryptographicException)
                    {
                        // The key is corrupt which can cause invalid padding to the stream. 
                        return null;
                    }


                    byte[] decrypted = msDecrypt.ToArray();
                    string result = Encoding.UTF8.GetString(decrypted);
                    return result;
                }
            }
        }

    }

    /// <summary>
    /// Tracks requests from RPC clients while they are being authorised
    /// </summary>
    public class PendingRPCClient
    {
        public string ClientId;
        public string Hash;
        public List<string> KnownClientList;

        public PendingRPCClient(string clientId, string hash, List<string> knownClientList)
        {
            ClientId = clientId;
            Hash = hash;
            KnownClientList = knownClientList;
        }
    }

}
