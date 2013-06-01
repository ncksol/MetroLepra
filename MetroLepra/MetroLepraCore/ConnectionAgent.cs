﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MetroLepra.Model;
using Newtonsoft.Json.Linq;

namespace MetroLepra.Core
{
    public class ConnectionAgent
    {
        private const string AuthCookiesRegex = "lepro.sid=(.+?); domain=.leprosorium.ru; path=/,  lepro.uid=(.+?); domain=.leprosorium.ru; path=/,";
        private HttpClient _client;
        private static ConnectionAgent _instance;

        private Cookie _sessionIdCookie;
        private Cookie _userIdCookie;
        private bool _isAuthenticated;

        /// <summary>
        ///     Prevents a default instance of the <see cref="ConnectionAgent" /> class from being created.
        /// </summary>
        private ConnectionAgent()
        {
            _client = new HttpClient();
        }

        /// <summary>
        ///     Gets the current connection agent instance
        /// </summary>
        public static ConnectionAgent Current
        {
            get { return _instance ?? (_instance = new ConnectionAgent()); }
        }

        /// <summary>
        /// Is user logged in
        /// </summary>
        public bool IsAuthenticated
        {
            get { return _isAuthenticated; }
        }

        public async Task<LoginPageModel> GetLoginPage()
        {
            var response = await _client.GetAsync("http://leprosorium.ru/login/");
            if (!response.IsSuccessStatusCode)
                return null;

            var loginPageHtmlData = await response.Content.ReadAsStringAsync();
            return HtmlParser.ParseLoginPage(loginPageHtmlData);
        }

        public async Task<MainPageModel> GetMainPage()
        {
            var response = await PerformAuthenticatedGetRequest("http://leprosorium.ru/");
            var responseContent = await response.Content.ReadAsStringAsync();
            return HtmlParser.ParseMainPage(responseContent);
        }

        public async Task<LeproPanelModel> GetLeproPanel()
        {
            var response = await PerformAuthenticatedGetRequest("http://leprosorium.ru/api/lepropanel");
            var responseContent = await response.Content.ReadAsStringAsync();

            var leproPanelModel = JsonParser.ParseLeproPanel(responseContent);
            return leproPanelModel;
        }

        public async Task<List<SubLepraModel>> GetUnderground(int? page = null)
        {
            var response = await PerformAuthenticatedGetRequest("http://leprosorium.ru/underground/subscribers/" + page.GetValueOrDefault(1));
            var responseContent = await response.Content.ReadAsStringAsync();

            var underground = HtmlParser.ParseUnderground(responseContent);
            return underground;
        }

        public async Task<DemocracyPageModel> GetDemocracyPage()
        {
            var response = await PerformAuthenticatedGetRequest("http://leprosorium.ru/democracy/");
            var responseContent = await response.Content.ReadAsStringAsync();

            var model = HtmlParser.ParseDemocracyPage(responseContent);
            return model;
        }

        public async Task<List<PostModel>> GetLatestPosts(int? page)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, new Uri("http://leprosorium.ru/idxctl/"));
            message.Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
                                                            {
                                                                new KeyValuePair<string, string>("from", page.GetValueOrDefault(0).ToString())
                                                            });

            var response = await PerformAuthenticatedPostRequest(message);
            var responseContent = await response.Content.ReadAsStringAsync();
            var posts = JsonParser.ParsePosts(responseContent);

            return posts;
        }

        public async Task<Stream> GetImageStream(String path)
        {
            var response = await _client.GetAsync(path);
            if (!response.IsSuccessStatusCode)
                return null;

            var imageStream = await response.Content.ReadAsStreamAsync();
            return imageStream;
        }

        /// <summary>
        /// Attempts to login user with specified credentials
        /// </summary>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <param name="captcha"></param>
        /// <param name="loginCode"></param>
        /// <returns>Error if login is unsuccessful</returns>
        public async Task<String> Login(String username, String password, String captcha, String loginCode)
        {
            var postString = String.Format("user={0}&pass={1}&captcha={2}&logincode={3}&save=1", username, password, captcha, loginCode);
            var byteArray = Encoding.UTF8.GetBytes(postString);

            var httpWebRequest = (HttpWebRequest)WebRequest.Create("http://leprosorium.ru/login/");
            httpWebRequest.AllowAutoRedirect = false;
            httpWebRequest.AllowReadStreamBuffering = true;
            httpWebRequest.Method = HttpMethod.Post.Method;
            httpWebRequest.ContentType = "application/x-www-form-urlencoded";
            httpWebRequest.ContentLength = byteArray.Length;

            var requestStream = await httpWebRequest.GetRequestStreamAsync();
            requestStream.Write(byteArray, 0, byteArray.Length);
            requestStream.Close();

            var response = await httpWebRequest.GetResponseAsync();
            var setCookie = response.Headers["Set-Cookie"];

            var success = SetAuthCookies(setCookie);
            if (!success)
            {
                using (var responseContentStream = response.GetResponseStream())
                {
                    var reader = new StreamReader(responseContentStream);
                    var responseContent = await reader.ReadToEndAsync();
                    return HtmlParser.ExtractLoginError(responseContent);
                }
            }

            _isAuthenticated = true;
            return String.Empty;
        }

        private bool SetAuthCookies(string setCookie)
        {
            var authCookiesMatch = Regex.Match(setCookie, AuthCookiesRegex);
            if (!authCookiesMatch.Groups[1].Success || !authCookiesMatch.Groups[2].Success)
                return false;

            var sessionId = authCookiesMatch.Groups[1].Value;
            var userId = authCookiesMatch.Groups[2].Value;

            _sessionIdCookie = new Cookie("lepro.sid", sessionId);
            _userIdCookie = new Cookie("lepro.uid", userId);

            return true;
        }

        private CookieContainer GetAuthCookiesContainer()
        {
            var cookieContainer = new CookieContainer();
            cookieContainer.Add(new Uri("http://leprosorium.ru/"), _sessionIdCookie);
            cookieContainer.Add(new Uri("http://leprosorium.ru/"), _userIdCookie);
            return cookieContainer;
        }

        private async Task<HttpResponseMessage> PerformAuthenticatedGetRequest(String url, bool isValidateStatusCode = true)
        {
            if (!IsAuthenticated)
                return null;

            var cookieContainer = GetAuthCookiesContainer();

            var handler = new HttpClientHandler { CookieContainer = cookieContainer };
            _client = new HttpClient(handler);

            var response = await _client.GetAsync(url);

            if (isValidateStatusCode)
                return !response.IsSuccessStatusCode ? null : response;
            else
                return response;
        }

        private async Task<HttpResponseMessage> PerformAuthenticatedPostRequest(HttpRequestMessage message)
        {
            if (!IsAuthenticated)
                return null;

            var cookieContainer = GetAuthCookiesContainer();

            var handler = new HttpClientHandler { CookieContainer = cookieContainer };
            _client = new HttpClient(handler);

            var response = await _client.SendAsync(message);
            return !response.IsSuccessStatusCode ? null : response;
        }
    }

    public static class HttpExtensions
    {
        public static Task<Stream> GetRequestStreamAsync(this HttpWebRequest request)
        {
            var taskComplete = new TaskCompletionSource<Stream>();
            request.BeginGetRequestStream(ar =>
                                              {
                                                  var requestStream = request.EndGetRequestStream(ar);
                                                  taskComplete.TrySetResult(requestStream);
                                              }, request);
            return taskComplete.Task;
        }

        public static Task<HttpWebResponse> GetResponseAsync(this HttpWebRequest request)
        {
            var taskComplete = new TaskCompletionSource<HttpWebResponse>();
            request.BeginGetResponse(asyncResponse =>
                                         {
                                             try
                                             {
                                                 var responseRequest = (HttpWebRequest) asyncResponse.AsyncState;
                                                 var someResponse = (HttpWebResponse) responseRequest.EndGetResponse(asyncResponse);
                                                 taskComplete.TrySetResult(someResponse);
                                             }
                                             catch (WebException webExc)
                                             {
                                                 var failedResponse = (HttpWebResponse) webExc.Response;
                                                 taskComplete.TrySetResult(failedResponse);
                                             }
                                         }, request);
            return taskComplete.Task;
        }
    }
}