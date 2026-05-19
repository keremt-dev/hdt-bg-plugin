using System;
using System.Net;
using System.Text;
using System.Threading;

namespace HsDecktrackBgReader
{
    /// <summary>
    /// Tiny single-endpoint HTTP server backed by HttpListener. Browser-facing
    /// contract is documented in plugin/README.md:
    ///
    ///   GET /lobby
    ///     200 application/json  → { "banned": int[], "heroes": string[] }
    ///     204 No Content        → plugin alive, no lobby state yet
    ///
    /// Always sends Access-Control-Allow-Origin: * so an index.html opened via
    /// file:// can talk to it. Single background thread; SetState is lock-free
    /// (single volatile reference swap).
    /// </summary>
    internal class LocalHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _thread;
        private volatile string _stateJson;
        private volatile bool _stopping;

        public LocalHttpServer(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:" + port + "/");
            _thread = new Thread(Loop) { IsBackground = true, Name = "HsDecktrackBgReader.HttpListener" };
        }

        public void Start()
        {
            _listener.Start();
            _thread.Start();
        }

        /// <summary>
        /// Replace the served state. Pass null to make the endpoint answer 204.
        /// JSON should already be serialized — extractor owns the format.
        /// </summary>
        public void SetState(string json)
        {
            _stateJson = json;
        }

        private void Loop()
        {
            while (!_stopping)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (HttpListenerException) { return; }     // listener closed
                catch (ObjectDisposedException) { return; }
                catch { continue; }

                try { Handle(ctx); }
                catch
                {
                    try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Cache-Control"] = "no-store";

            // CORS preflight — browsers shouldn't need it for a simple GET, but
            // be permissive in case index.html is hosted on a real origin.
            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                res.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
                res.StatusCode = 204;
                res.Close();
                return;
            }

            if (ctx.Request.HttpMethod != "GET" || ctx.Request.Url.AbsolutePath != "/lobby")
            {
                res.StatusCode = 404;
                res.Close();
                return;
            }

            var snapshot = _stateJson;
            if (string.IsNullOrEmpty(snapshot))
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(snapshot);
            res.StatusCode = 200;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        public void Dispose()
        {
            _stopping = true;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { if (_thread.IsAlive) _thread.Join(500); } catch { }
        }
    }
}
