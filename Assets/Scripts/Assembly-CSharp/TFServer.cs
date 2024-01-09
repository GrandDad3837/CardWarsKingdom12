#define ASSERTS_ON
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Text;
using MiniJSON;
using UnityEngine;

public class TFServer
{
	public delegate void JsonStringHandler(string jsonResponse, HttpStatusCode status);

	public delegate void JsonResponseHandler(Dictionary<string, object> dict, HttpStatusCode status);

	public const string ERROR_KEY = "error";

	public const string NETWORK_ERROR = "Network error";

	private const bool LOG_FAILED_REQUESTS = true;

	public static readonly string NETWORK_ERROR_JSON = "{\"success\": false, \"error\": \"Network error\"}";

	private static readonly string LOG_LOCATION = Application.persistentDataPath + Path.DirectorySeparatorChar + "error";

	private static int errorCount = 0;

	private CookieContainer cookies = new CookieContainer();

	private Dictionary<object, JsonStringHandler> reqs = new Dictionary<object, JsonStringHandler>();

	private bool shortCircuitRequests;

	public TFServer(CookieContainer cookies, int maxConnections)
	{
		this.cookies = cookies;
		TFWebClient.maxConnections = maxConnections;
	}

	public void ShortCircuitAllRequests()
	{
		shortCircuitRequests = true;
	}

	public void PostToJSON(string url, Dictionary<string, object> postDict, JsonResponseHandler callback, bool ignoreEtag = false)
	{
		string text = EncodePostData(postDict);
		TFWebClient tFWebClient = RegisterCallback(callback);
		if (shortCircuitRequests)
		{
			Debug.Log("shortcircuiting a post to " + url);
			GetCallback(tFWebClient)(NETWORK_ERROR_JSON, HttpStatusCode.ServiceUnavailable);
			tFWebClient.Dispose();
			return;
		}
		Debug.Log("posting data to " + url);
		Debug.Log("post data: " + text);
		tFWebClient.Headers.Add("Content-Type", "application/x-www-form-urlencoded");
		string text2 = SessionManager.Instance.theSession.WebFileServer.ReadETag();
		if (!ignoreEtag && text2 != null)
		{
			tFWebClient.Headers.Add(HttpRequestHeader.IfMatch, text2);
		}
		tFWebClient.UploadStringCompleted += OnUploadComplete;
		tFWebClient.UploadStringAsync(new Uri(url), text);
	}

	public void PostToString(string url, Dictionary<string, object> postDict, JsonStringHandler callback)
	{
		string text = EncodePostData(postDict);
		TFWebClient tFWebClient = RegisterCallback(callback);
		if (shortCircuitRequests)
		{
			Debug.Log("shortcircuiting a post to " + url);
			GetCallback(tFWebClient)("Network error", HttpStatusCode.ServiceUnavailable);
			tFWebClient.Dispose();
		}
		else
		{
			Debug.Log("posting data to " + url);
			Debug.Log("post data: " + text);
			tFWebClient.Headers.Add("Content-Type", "application/x-www-form-urlencoded");
			tFWebClient.UploadStringCompleted += OnUploadComplete;
			tFWebClient.UploadStringAsync(new Uri(url), text);
		}
	}

	public void GetToJSON(string url, JsonResponseHandler callback)
	{
		TFWebClient tFWebClient = RegisterCallback(callback);
		if (shortCircuitRequests)
		{
			Debug.Log("Shortcircuiting a request to " + url);
			GetCallback(tFWebClient)(NETWORK_ERROR_JSON, HttpStatusCode.ServiceUnavailable);
			tFWebClient.Dispose();
		}
		else
		{
			Debug.Log("making request to " + url);
			tFWebClient.DownloadStringCompleted += OnDownloadComplete;
			tFWebClient.DownloadStringAsync(new Uri(url));
		}
	}

	public Cookie GetCookie(Uri uri, string key)
	{
		return cookies.GetCookies(uri)[key];
	}

	private TFWebClient RegisterCallback(JsonStringHandler callback)
	{
		TFWebClient tFWebClient = new TFWebClient(cookies);
		reqs[tFWebClient] = callback;
		tFWebClient.NetworkError += OnNetworkError;
		return tFWebClient;
	}

	private TFWebClient RegisterCallback(JsonResponseHandler callback)
	{
		TFWebClient tFWebClient = new TFWebClient(cookies);
		reqs[tFWebClient] = JsCallback(callback);
		tFWebClient.NetworkError += OnNetworkError;
		return tFWebClient;
	}

	private string EncodePostData(Dictionary<string, object> d)
	{
		List<string> list = new List<string>();
		foreach (KeyValuePair<string, object> item in d)
		{
			string s = item.Value.ToString();
			list.Add(item.Key + "=" + WWW.EscapeURL(s));
		}
		return string.Join("&", list.ToArray());
	}

	private JsonStringHandler JsCallback(JsonResponseHandler cb)
	{
		return delegate(string jsonResponse, HttpStatusCode code)
		{
			Dictionary<string, object> dict = (Dictionary<string, object>)Json.Deserialize(jsonResponse);
			cb(dict, code);
		};
	}

	private void OnNetworkError(object sender, WebException e)
	{
		if (e.Response != null)
		{
			LogResponse(e.Response as HttpWebResponse);
		}
		if (e.Response != null)
		{
			GetCallback(sender)(NETWORK_ERROR_JSON, ((HttpWebResponse)e.Response).StatusCode);
		}
		else
		{
			GetCallback(sender)(NETWORK_ERROR_JSON, HttpStatusCode.ServiceUnavailable);
		}
	}

	private void OnDownloadComplete(object sender, DownloadStringCompletedEventArgs e)
	{
		Debug.Log("in onDownloadComplete...");
		JsonStringHandler jsonStringHandler = OnRequestComplete(sender, e);
		if (jsonStringHandler == null)
		{
			return;
		}
		if (!e.Result.Contains("\"ip\":")) Debug.Log("web result: " + e.Result);
		string result = e.Result;
		if (e.Error == null)
		{
			jsonStringHandler(result, HttpStatusCode.OK);
			return;
		}
		WebException ex = e.Error as WebException;
		if (ex != null)
		{
			jsonStringHandler(result, ((HttpWebResponse)ex.Response).StatusCode);
		}
		else
		{
			jsonStringHandler(result, HttpStatusCode.Unused);
		}
	}

	private void OnUploadComplete(object sender, UploadStringCompletedEventArgs e)
	{
		Debug.Log("in onUploadComplete...");
		JsonStringHandler jsonStringHandler = OnRequestComplete(sender, e);
		if (jsonStringHandler == null)
		{
			return;
		}
		if (e.Error == null)
		{
			jsonStringHandler(e.Result, HttpStatusCode.OK);
			return;
		}
		WebException ex = e.Error as WebException;
		if (ex != null)
		{
			jsonStringHandler(e.Result, ((HttpWebResponse)ex.Response).StatusCode);
		}
		else
		{
			jsonStringHandler(e.Result, HttpStatusCode.Unused);
		}
	}

	private JsonStringHandler OnRequestComplete(object sender, AsyncCompletedEventArgs e)
	{
		Debug.Log("in onRequestCompete..." + e.Error);
		JsonStringHandler callback = GetCallback(sender);
		TFUtils.Assert(null != e, "No event args happened.");
		if (e.Error != null && callback != null)
		{
			WebException ex = e.Error as WebException;
			if (ex != null)
			{
				if (ex != null && ex.Response != null)
				{
					LogResponse((HttpWebResponse)ex.Response, ex.Status);
				}
				callback(NETWORK_ERROR_JSON, (ex.Response == null) ? HttpStatusCode.RequestTimeout : ((HttpWebResponse)ex.Response).StatusCode);
			}
			else
			{
				callback(NETWORK_ERROR_JSON, HttpStatusCode.Unused);
			}
		}
		else if (!e.Cancelled)
		{
			return callback;
		}
		return null;
	}

	private JsonStringHandler GetCallback(object sender)
	{
		if (reqs.ContainsKey(sender))
		{
			JsonStringHandler result = reqs[sender];
			reqs.Remove(sender);
			return result;
		}
		return null;
	}

	private void LogResponse(HttpWebResponse response, WebExceptionStatus? status = null)
	{
		if (response != null)
		{
			Stream responseStream = response.GetResponseStream();
			using (StreamReader streamReader = new StreamReader(responseStream, Encoding.UTF8))
			{
				string text = streamReader.ReadToEnd();
			}
			responseStream.Dispose();
		}
	}
}
