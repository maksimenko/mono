//
// HttpReplyChannel.cs
//
// Author:
//	Atsushi Enomoto <atsushi@ximian.com>
//
// Copyright (C) 2006 Novell, Inc.  http://www.novell.com
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.ServiceModel;
using System.Text;
using System.Threading;

namespace System.ServiceModel.Channels
{
	internal class HttpReplyChannel : ReplyChannelBase
	{
		HttpChannelListener<IReplyChannel> source;
		List<HttpListenerContext> waiting = new List<HttpListenerContext> ();
		TimeSpan timeout;
		EndpointAddress local_address;

		public HttpReplyChannel (HttpChannelListener<IReplyChannel> listener,
			TimeSpan timeout)
			: base (listener)
		{
			this.source = listener;
			this.timeout = timeout;
		}

		public MessageEncoder Encoder {
			get { return source.MessageEncoder; }
		}

		// FIXME: where is it set?
		public override EndpointAddress LocalAddress {
			get { return local_address; }
		}

		public override bool TryReceiveRequest (TimeSpan timeout, out RequestContext context)
		{
			context = null;
			if (waiting.Count == 0 && !WaitForRequest (timeout))
				return false;
			HttpListenerContext ctx = null;
			lock (waiting) {
				if (waiting.Count > 0) {
					ctx = waiting [0];
					waiting.RemoveAt (0);
				}
			}
			if (ctx == null) 
				// Though as long as this instance is used
				// synchronously, it should not happen.
				return false;

			// FIXME: supply maxSizeOfHeaders.
			int maxSizeOfHeaders = 0x10000;

			Message msg = null;
			if (ctx.Request.HttpMethod == "POST") {
				if (!Encoder.IsContentTypeSupported (ctx.Request.ContentType)) {
					ctx.Response.StatusCode = (int) HttpStatusCode.UnsupportedMediaType;
					ctx.Response.StatusDescription = String.Format (
							"Expected content-type '{0}' but got '{1}'", Encoder.ContentType, ctx.Request.ContentType);
					ctx.Response.Close ();

					return false;
				}

				msg = Encoder.ReadMessage (
					ctx.Request.InputStream, maxSizeOfHeaders);
				if (source.MessageEncoder.MessageVersion.Envelope == EnvelopeVersion.Soap11 ||
				    source.MessageEncoder.MessageVersion.Addressing == AddressingVersion.None) {
					string action = GetHeaderItem (ctx.Request.Headers ["SOAPAction"]);
					if (action != null)
						msg.Headers.Action = action;
				}
			} else if (ctx.Request.HttpMethod == "GET") {
				msg = Message.CreateMessage (source.MessageEncoder.MessageVersion, null);
			}
			msg.Headers.To = ctx.Request.Url;
			
			HttpRequestMessageProperty prop =
				new HttpRequestMessageProperty ();
			prop.Method = ctx.Request.HttpMethod;
			prop.QueryString = ctx.Request.Url.Query;
			if (prop.QueryString.StartsWith ("?"))
				prop.QueryString = prop.QueryString.Substring (1);
			// FIXME: prop.SuppressEntityBody
			prop.Headers.Add (ctx.Request.Headers);
			msg.Properties.Add (HttpRequestMessageProperty.Name, prop);
/*
MessageBuffer buf = msg.CreateBufferedCopy (0x10000);
msg = buf.CreateMessage ();
System.Xml.XmlTextWriter w = new System.Xml.XmlTextWriter (Console.Out);
w.Formatting = System.Xml.Formatting.Indented;
buf.CreateMessage ().WriteMessage (w);
w.Close ();
*/
			context = new HttpRequestContext (this, msg, ctx);
			return true;
		}

		protected string GetHeaderItem (string raw)
		{
			if (raw == null || raw.Length == 0)
				return raw;
			switch (raw [0]) {
			case '\'':
			case '"':
				if (raw [raw.Length - 1] == raw [0])
					return raw.Substring (1, raw.Length - 2);
				// FIXME: is it simply an error?
				break;
			}
			return raw;
		}

		public override IAsyncResult BeginTryReceiveRequest (TimeSpan timeout, AsyncCallback callback, object state)
		{
			throw new NotImplementedException ();
		}

		public override bool EndTryReceiveRequest (IAsyncResult result, out RequestContext context)
		{
			throw new NotImplementedException ();
		}

		public override bool WaitForRequest (TimeSpan timeout)
		{
			AutoResetEvent wait = new AutoResetEvent (false);
			try {
				source.Http.BeginGetContext (HttpContextReceived, wait);
			} catch (HttpListenerException e) {
				if (e.ErrorCode == 0x80004005) // invalid handle. Happens during shutdown.
					while (true) Thread.Sleep (1000); // thread is about to be terminated.
				throw;
			} catch (ObjectDisposedException) { return false; }
			// FIXME: we might want to take other approaches.
			if (timeout.Ticks > int.MaxValue)
				timeout = TimeSpan.FromDays (20);
			return wait.WaitOne (timeout, false);
		}

		void HttpContextReceived (IAsyncResult result)
		{
			if (State == CommunicationState.Closing || State == CommunicationState.Closed)
				return;

			waiting.Add (source.Http.EndGetContext (result));
			AutoResetEvent wait = (AutoResetEvent) result.AsyncState;
			wait.Set ();
		}

		public override IAsyncResult BeginWaitForRequest (TimeSpan timeout, AsyncCallback callback, object state)
		{
			throw new NotImplementedException ();
		}

		public override bool EndWaitForRequest (IAsyncResult result)
		{
			throw new NotImplementedException ();
		}

		public override RequestContext ReceiveRequest (TimeSpan timeout)
		{
			RequestContext ctx;
			TryReceiveRequest (timeout, out ctx);
			return ctx;
		}

		public override IAsyncResult BeginReceiveRequest (TimeSpan timeout, AsyncCallback callback, object state)
		{
			throw new NotImplementedException ();
		}

		public override RequestContext EndReceiveRequest (IAsyncResult result)
		{
			throw new NotImplementedException ();
		}

		protected override void OnAbort ()
		{
			foreach (HttpListenerContext ctx in waiting)
				ctx.Request.InputStream.Close ();
		}

		protected override void OnClose (TimeSpan timeout)
		{
			// FIXME: consider timeout
			foreach (HttpListenerContext ctx in waiting)
				ctx.Request.InputStream.Close ();
		}

		protected override void OnOpen (TimeSpan timeout)
		{
		}
	}
}