/*<FILE_LICENSE>
 * Azos (A to Z Application Operating System) Framework
 * The A to Z Foundation (a.k.a. Azist) licenses this file to you under the MIT license.
 * See the LICENSE file in the project root for more information.
</FILE_LICENSE>*/

using System;
using System.Linq;
using System.Text;
using System.Threading;

using Azos.Apps;
using Azos.Conf;
using Azos.Instrumentation;
using Azos.Serialization.JSON;

namespace Azos.Web.Social
{
  #warning Remove this shit
    public static class SocialStringConsts
    {
      public const string POSTFAILED_ERROR = "Social network post to failed";
      public const string INVALID_STATE_ERROR = "Expected state is '{0}' but current state is '{1}'. ";
    }

    /// <summary>
    /// Defines an abstraction for social networks
    /// </summary>
    public abstract class SocialNetwork : DaemonWithInstrumentation<IApplicationComponent>, IWebClientCaller, ISocialNetworkImplementation
    {
      #region Const

        public const int DEFAULT_TIMEOUT_MS_DEFAULT = 30 * 1000;

        public const string SOCIAL_PARAMNAME = "social";
        public const string SOCIAL_ACTION_PARAMNAME = "socialaction";
        public const string SOCIALACTION_SPECIFYURL_PARAMVALUE = "specifyurl";

        public const string SOCIALPOST_PARAMNAME = "socialpost";
        public const string SOCIALPOSTMESSAGE_PARAMNAME = "message";

        private static readonly TimeSpan INSTR_INTERVAL = TimeSpan.FromMilliseconds(3700);
      #endregion

      #region Static

        protected static string GenerateNonce()
        {
          byte[] byteOAuthNonce = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
          return Guid.NewGuid().ToString().Replace("-", string.Empty);
        }
      #endregion


      #region ctor

        protected SocialNetwork(IApplication app) : base(app) => ctor();
        protected SocialNetwork(IApplicationComponent director) : base(director) => ctor();

        private void ctor()
        {
          m_WebServiceCallTimeoutMs = DEFAULT_TIMEOUT_MS_DEFAULT;
          KeepAlive = true;
          Pipelined = true;
          GrantViewEmail = true;
          GrantPost = true;
          GrantAccessProfile = true;
          GrantAccessFriends = true;
        }

        protected override void Destructor()
        {
          DisposableObject.DisposeAndNull(ref m_InstrumentationEvent);
          base.Destructor();
        }

      #endregion

      #region fields

        private bool m_InstrumentationEnabled;
        private Time.Event m_InstrumentationEvent;

        private int m_WebServiceCallTimeoutMs;

        private int m_stat_Login;
        private int m_stat_LoginErr;
        private int m_stat_RenewLongTermToken;
        private int m_stat_PostMessage;

      #endregion

      #region Properties

        public override string ComponentLogTopic => CoreConsts.SOCIAL_TOPIC;

        public override string ComponentCommonName { get { return GetType().Name; }}

        /// <summary>
        /// Implements IInstrumentable
        /// </summary>
        [Config]
        [ExternalParameter(CoreConsts.EXT_PARAM_GROUP_INSTRUMENTATION, CoreConsts.EXT_PARAM_GROUP_SOCIAL)]
        public override bool InstrumentationEnabled
        {
          get { return m_InstrumentationEnabled;}
          set
          {
             m_InstrumentationEnabled = value;
             if (m_InstrumentationEvent==null)
             {
               if (!value) return;
               resetStats();
               m_InstrumentationEvent = new Time.Event(App.EventTimer, null, e => AcceptManagerVisit(this, e.LocalizedTime), INSTR_INTERVAL);
             }
             else
             {
               if (value) return;
               DisposableObject.DisposeAndNull(ref m_InstrumentationEvent);
             }
          }
        }

        /// <summary>
        /// Globally uniquelly identifies social network architype
        /// </summary>
        public abstract SocialNetID ID { get; }

        /// <summary>
        /// Provides social network description, this default implementation returns the name of the class
        /// </summary>
        public virtual string Description
        {
          get { return Name + ": " + this.GetType().Name; }
        }


        /// <summary>
        /// Returns the root public URL for the service
        /// </summary>
        public abstract string ServiceURL { get; }

        /// <summary>
        /// Specifies how service takes user credentials
        /// </summary>
        public abstract CredentialsEntryMethod CredentialsEntry { get; }

        /// <summary>
        /// Defines if a meeesage can be post to this social network
        /// </summary>
        public virtual bool CanPost { get { return false;} }

        /// <summary>
        /// Sets timeout for calls to external service that imlements this social network
        /// </summary>
        [Config]
        public int WebServiceCallTimeoutMs
        {
          get { return m_WebServiceCallTimeoutMs; }
          set { m_WebServiceCallTimeoutMs = value < 0 ? 0 : value; }
        }

        /// <summary>
        /// Sets if pipelining should be used for web request
        /// </summary>
        [Config(Default=true)]
        public bool KeepAlive { get; set; }

        /// <summary>
        /// Sets keepalive option for web request
        /// </summary>
        [Config(Default = true)]
        public bool Pipelined { get; set; }

        [Config(Default = true)]
        public bool GrantViewEmail { get; set; }

        [Config(Default = true)]
        public bool GrantPost { get; set; }

        [Config(Default = true)]
        public bool GrantAccessProfile { get; set; }

        [Config(Default = true)]
        public bool GrantAccessFriends { get; set; }
      #endregion

      #region Public
        /// <summary>
        /// Returns href to login via social system/site
        /// </summary>
        public abstract string GetExternalLoginReference(string returnURL);

        /// <summary>
        /// Returns social service login URL for "two-stage" login networks.
        /// Currently twitter only requires this
        /// </summary>
        /// <param name="returnURL">Social site redirects browser here after login</param>
        /// <param name="userInfo">Context</param>
        /// <returns>Social site login URL</returns>
        public virtual string GetSpecifiedExternalLoginReference(SocialUserInfo userInfo, string returnURL)
        {
          throw new SocialException(StringConsts.OPERATION_NOT_SUPPORTED_ERROR + GetType().Name + ".GetSpecifiedExternalLoginReference");
        }

        /// <summary>
        /// Specifies if this provider requires to obtain temporary token before redirecting to social network login page.
        /// Currently only Twitter requires this routine
        /// </summary>
        public virtual bool RequiresSpecifiedExternalLoginReference { get { return false; } }

        /// <summary>
        /// Fills user info with values from social network
        /// </summary>
        /// <param name="userInfo">Context user info</param>
        /// <param name="request">Context http request</param>
        /// <param name="returnURL">Social network login URL (sometimes needed by social site just to ensure correct call)</param>
        public void ObtainTokensAndFillInfo(SocialUserInfo userInfo, JSONDataMap request, string returnURL)
        {
          try
          {
            if (userInfo.LoginState != SocialLoginState.NotLoggedIn)
              throw new SocialException(SocialStringConsts.INVALID_STATE_ERROR.Args(SocialLoginState.NotLoggedIn, userInfo.LoginState)
                + GetType().Name + ".ObtainTokensAndFillInfo");

            DoObtainTokens(userInfo, request, returnURL);
            userInfo.LastError = null;
            userInfo.LoginState = SocialLoginState.TokenObtained;

            DoRetrieveLongTermTokens(userInfo);
            userInfo.LoginState = SocialLoginState.LongTermTokenObtained;

            DoRetrieveUserInfo(userInfo);
            userInfo.LoginState = SocialLoginState.LoggedIn;

            if (m_InstrumentationEnabled) Interlocked.Increment(ref m_stat_Login);
          }
          catch (Exception ex)
          {
            userInfo.LastError = ex;
            if (m_InstrumentationEnabled) Interlocked.Increment(ref m_stat_LoginErr);
          }
        }

        /// <summary>
        /// Refreshes long term tokens (if provider needs them).
        /// Should be used in scenario like background server-side token renew routine
        /// </summary>
        public void RenewLongTermTokens(SocialUserInfo userInfo)
        {
          if (userInfo.LoginState != SocialLoginState.TokenObtained)
            throw new SocialException(SocialStringConsts.INVALID_STATE_ERROR.Args(SocialLoginState.TokenObtained, userInfo.LoginState)
              + GetType().Name + ".RenewLongTermTokens");

          DoRetrieveLongTermTokens(userInfo);

          if (m_InstrumentationEnabled) Interlocked.Increment(ref m_stat_RenewLongTermToken);
        }

        /// <summary>
        /// Retrieves all user fields (e.g. screen name, email) but tokens.
        /// </summary>
        public void RetrieveUserInfo(SocialUserInfo userInfo)
        {
          if (userInfo.LoginState != SocialLoginState.LongTermTokenObtained)
            throw new SocialException(SocialStringConsts.INVALID_STATE_ERROR.Args(SocialLoginState.LongTermTokenObtained, userInfo.LoginState)
              + GetType().Name + ".RetrieveUserInfo");

          DoRetrieveUserInfo(userInfo);
        }

        protected abstract void DoObtainTokens(SocialUserInfo userInfo, JSONDataMap request, string returnURL);

        protected abstract void DoRetrieveLongTermTokens(SocialUserInfo userInfo);

        protected abstract void DoRetrieveUserInfo(SocialUserInfo userInfo);

        /// <summary>
        /// Create an instance of social user info class.
        /// If parameters are null then creates new non-logged-in instance, otherwise, if parameters are set,
        /// then connects to network and tries to re-initializes SocialUser info with fresh data
        /// from the network (i.e. name, gender etc.) using the supplied net tokens, or throws if tokens are invalid (i.e. expired).
        /// This returned instance is usually stored in session for later use
        /// </summary>
        /// <returns>SocialUserInfo instance</returns>
        public abstract SocialUserInfo CreateSocialUserInfo(SocialUserInfoToken? existingToken = null);

        /// <summary>
        /// Returns user profile image data along with content type or null if no image available.
        /// Picture kind specifies classification of pictures within profile i.e. "main", "small-icon" etc.
        /// </summary>
        public virtual byte[] GetPictureData(SocialUserInfo userInfo, out string contentType, string pictureKind = null)
        {
          contentType = string.Empty;

          if (userInfo.PictureLink.IsNullOrWhiteSpace()) return null;

          return WebClient.GetData(userInfo.PictureLink, new WebClient.RequestParams(this), out contentType);
        }

        /// <summary>
        /// Returns user profile image or null if no image available.
        /// Picture kind specifies classification of pictures within profile i.e. "main", "small-icon" etc.
        /// </summary>
        public Azos.Graphics.Image GetPicture(SocialUserInfo userInfo, out string contentType, string pictureKind = null)
        {
          var data = GetPictureData(userInfo, out contentType, pictureKind);
          if (data == null) return null;
          return Azos.Graphics.Image.FromBytes(data);
        }

        /// <summary>
        /// Post message to social network
        /// </summary>
        /// <param name="userInfo">Context social user info</param>
        /// <param name="text">Message to send</param>
        public void PostMessage(SocialUserInfo userInfo, string text)
        {
          try
          {
            DoPostMessage(text, userInfo);
            userInfo.LastError = null;

            if (m_InstrumentationEnabled) Interlocked.Increment(ref m_stat_PostMessage);
          }
          catch (System.Net.WebException ex)
          {
            String responseString;
            using (System.IO.Stream stream = ex.Response.GetResponseStream())
            {
              System.IO.StreamReader reader = new System.IO.StreamReader(stream, Encoding.UTF8);
              responseString = reader.ReadToEnd();
            }

            userInfo.LastError = new SocialException("{0}\r\n{1}".Args(ex.Message, responseString));
          }
          catch (Exception ex)
          {
            userInfo.LastError = ex;
          }
        }

        protected virtual void DoPostMessage(string text, SocialUserInfo userInfo) {}

        public override string ToString()
        {
          return "{0} ({1})".Args(Name, Description);
        }

        public override bool Equals(object obj)
        {
          var other = obj as SocialNetwork;
          return other==null ? false : this.Name == other.Name;
        }

        public override int GetHashCode()
        {
          return Name.GetHashCode();
        }
#pragma warning disable 1570
        /// <summary>
        /// Takes unescaped regular URL and transforms it in a single escaped parameter string suitable
        /// for submission to social network.
        /// For example, incoming "https://aa.bb?nonce=FDAC25&target=123" -> "https%3a%2f%2faa.com%3fnonce%3dFDAC25%26target%3d123"
        /// </summary>
#pragma warning restore 1570
        public string PrepareReturnURLParameter(string returnURL, bool escape = true)
        {
          var query = returnURL.IndexOf('?') > 0;

          if (!query) returnURL += "?";
          else returnURL += "&";

          returnURL += SOCIAL_PARAMNAME + "=" + Name;

          if (escape) returnURL = Uri.EscapeDataString(returnURL);
          return returnURL;
        }

      #endregion

      #region Protected
        protected override void DoAcceptManagerVisit(object manager, DateTime managerNow)
        {
          dumpStats();
        }
      #endregion

      #region .pvt .impl
                    private void dumpStats()
                    {
                      var src = this.Name;

                      Instrumentation.LoginCount.Record(src, m_stat_Login);
                      m_stat_Login = 0;

                      Instrumentation.LoginErrorCount.Record(src, m_stat_LoginErr);
                      m_stat_LoginErr = 0;

                      Instrumentation.RenewLongTermTokenCount.Record(src, m_stat_RenewLongTermToken);
                      m_stat_RenewLongTermToken = 0;

                      Instrumentation.PostMsgCount.Record(src, m_stat_PostMessage);
                      m_stat_PostMessage = 0;
                    }

                    private void resetStats()
                    {
                      m_stat_Login = 0;
                      m_stat_LoginErr = 0;
                      m_stat_RenewLongTermToken = 0;
                      m_stat_PostMessage = 0;
                    }
      #endregion

    }
}
