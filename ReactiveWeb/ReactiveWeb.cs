﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
using UniRx;
#else
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
#endif

/// <summary>
/// HTTP/1.0
/// http://www.spencernetwork.org/reference/rfc1945-ja-HTTP1.0.txt
/// 
/// HTTP/1.1
/// 
/// https://www.ietf.org/rfc/rfc2068.txt
/// http://www.spencernetwork.org/reference/rfc2068-ja-HTTP1.1.txt
/// https://www.ietf.org/rfc/rfc2616.txt
/// http://www.spencernetwork.org/reference/rfc2616-ja-HTTP1.1.txt
/// </summary>
namespace ReactiveWeb
{
    /// <summary>
    /// http://blogs.msdn.com/b/toub/archive/2006/04/12/blocking-queues.aspx
    /// </summary>
    /// <typeparam name="T"></typeparam>
    class BlockingQueue<T>
    {
        private int _count = 0;
        private Queue<T> _queue = new Queue<T>();

        public T Dequeue()
        {
            lock (_queue)
            {
                while (_count <= 0) Monitor.Wait(_queue);
                _count--;
                return _queue.Dequeue();
            }
        }

        public void Enqueue(T data)
        {
            if (data == null) throw new ArgumentNullException("data");
            lock (_queue)
            {
                _queue.Enqueue(data);
                _count++;
                Monitor.Pulse(_queue);
            }
        }
    }

    class CustomNewThreadScheduler : IScheduler
    {
        BlockingQueue<Action> m_queue = new BlockingQueue<Action>();
        Thread m_thread;

        public CustomNewThreadScheduler()
        {
            m_thread = new Thread(PollingLoop);
            m_thread.IsBackground = true;
            m_thread.Name = "CustomNewThreadScheduler";
            m_thread.Start();
        }

        /// <summary>
        /// Polling loop
        /// </summary>
        void PollingLoop()
        {
            while (true)
            {
                // polling
                var action = m_queue.Dequeue();

                // exec
                //Logger.Log("Dequeue: " + m_thread.ManagedThreadId);
                action();
            }
        }

        public DateTimeOffset Now
        {
            get
            {
                return DateTimeOffset.Now;
            }
        }

        public IDisposable Schedule(Action action)
        {
            //Logger.Log("Enqueue: " + m_thread.ManagedThreadId);
            m_queue.Enqueue(action);
            return Disposable.Empty;
        }

        public IDisposable Schedule(TimeSpan dueTime, Action action)
        {
            throw new NotImplementedException();
        }


        public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        {
            throw new NotImplementedException();
        }

        public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        {
            throw new NotImplementedException();
        }

        public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
        {
            return Schedule(() => action(this, state));
        }
    }

    #region Observable
    static class IObservableExtensions
    {
        /// <summary>
        /// http://qiita.com/Temarin_PITA/items/efc1975d3e83287d8891
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="resource"></param>
        /// <param name="register"></param>
        /// <returns></returns>
        public static TResult AddTo<T, TResult>(this T resource, Func<T, TResult> register) where T : IDisposable
        {
            if (register == null)
                throw new ArgumentNullException(/*nameof(register)*/);
            return register(resource);
        }
        public static void AddTo<T>(this T resource, CompositeDisposable register) where T : IDisposable
        {
            if (register == null)
                throw new ArgumentNullException(/*nameof(register)*/);
            register.Add(resource);
        }

        /// <summary>
        /// https://gist.github.com/Manuel-S/1fad0455d849e1e2df6c
        /// </summary>
        public static IObservable<List<TSource>> BufferWhile<TSource>(
            this IObservable<TSource> source,
            Func<TSource, bool> condition)
        {
            return source.AggregateUntil(
                () => new List<TSource>(),
                (list, value) =>
                {
                    list.Add(value);
                    return list;
                },
                list => !condition(list[list.Count - 1]));
        }

        /// <summary>
        /// https://gist.github.com/Manuel-S/1fad0455d849e1e2df6c
        /// </summary>
        public static IObservable<TAccumulate> AggregateUntil<TSource, TAccumulate>(
            this IObservable<TSource> source,
            Func<TAccumulate> seed,
            Func<TAccumulate, TSource, TAccumulate> accumulator,
            Func<TAccumulate, bool> predicate)
        {
            return Observable.Create<TAccumulate>(observer =>
            {
                var accumulate = seed();
                return source.Subscribe(value =>
                {
                    accumulate = accumulator(accumulate, value);

                    if (predicate(accumulate))
                    {
                        observer.OnNext(accumulate);
                        accumulate = seed();
                    }
                },
                observer.OnError,
                observer.OnCompleted);
            });
        }
    }

    /// <summary>
    /// http://www.introtorx.com/content/v1.0.10621.0/15_SchedulingAndThreading.html
    /// </summary>
    sealed class StreamReaderState
    {
        private readonly Func<byte[], int, int, IObservable<int>> _factory;
        public StreamReaderState(Stream source, int bufferSize)
        {
            _factory = Observable.FromAsyncPattern<byte[], int, int, int>(
            source.BeginRead,
            source.EndRead);
            Buffer = new byte[bufferSize];
        }
        public IObservable<int> ReadNext()
        {
            return _factory(Buffer, 0, Buffer.Length);
        }
        public byte[] Buffer { get; set; }
    }

    static class StreamExtensions
    {
        /// <summary>
        /// http://www.introtorx.com/content/v1.0.10621.0/15_SchedulingAndThreading.html
        /// </summary>
        /// <param name="source"></param>
        /// <param name="buffersize"></param>
        /// <param name="scheduler"></param>
        /// <returns></returns>
        public static IObservable<byte> ToObservable(
        this Stream source,
        int buffersize,
        IScheduler scheduler)
        {
            var bytes = Observable.Create<byte>(o =>
            {
                var state = new StreamReaderState(source, buffersize);
                var currentStateSubscription = new SerialDisposable();
                Action<Action> iterator =
                (self) =>
                currentStateSubscription.Disposable = state.ReadNext()
                .Subscribe(
                bytesRead =>
                {
                    if (bytesRead > 0)
                    {
                        foreach(var b in state.Buffer.Take(bytesRead))
                        {
                            o.OnNext(b);
                        }
                        self();
                    }
                    else
                    {
                        o.OnCompleted();
                    }
                },
                o.OnError);
                var scheduledWork = scheduler.Schedule(iterator);
                return new CompositeDisposable(currentStateSubscription, scheduledWork);
            });
            return bytes;
        }
    }
#endregion

    #region HttpConnection
    public class HttpConnection : IDisposable
    {
        protected Uri m_uri;
        public Uri Uri
        {
            get { return m_uri; }
        }

        TcpClient m_client;

        protected Stream m_stream;
        public bool IsConnected
        {
            get
            {
                return m_client.Client.Connected;
            }
        }

        public HttpConnection(Uri uri)
        {
            m_uri = uri;
        }

        public static HttpConnection Create(Uri uri)
        {
            if (uri.Scheme == "https")
            {
                return new HttpsConnection(uri);
            }
            else
            {
                return new HttpConnection(uri);
            }
        }

        public IObservable<HttpConnection> ConnectObservable()
        {
            var client = new TcpClient();
            m_client = client;

            return
                Observable.FromAsyncPattern<string, int>(
                client.BeginConnect, client.EndConnect)(m_uri.Host, m_uri.Port)
                .Do(_ =>
                {
                    Status = String.Format("connected from {0} to {1}"
                        , client.Client.LocalEndPoint
                        , client.Client.RemoteEndPoint
                        );
                    m_stream=GetStream(client);
                    //m_disposable.Add(m_stream);
                    m_disposable.Add(client);
                })
                .Select(_ => this)
                ;
        }

        public IObservable<Unit> SendRequestObservable(HttpRequest request)
        {
            return
            from wait_for_write_request_header in Write(request.ToString())
            from wait_for_write_request_body in ((request.Method == MethodType.POST && request.PostData != null && request.PostData.Length > 0)
            ? Write(request.PostData)
            : Observable.Return(Unit.Default))
            select wait_for_write_request_body;
            ;
        }

        protected virtual Stream GetStream(TcpClient client)
        {
            var stream=client.GetStream();
            return stream;
        }    
       
        public String Status
        {
            get;
            private set;
        }

        public override string ToString()
        {
            return Status;
        }

        Subject<Byte> m_read;

        public IObservable<Byte> Read(int bufferSize, IScheduler scheduler)
        {
            if (m_read == null)
            {
                m_read = new Subject<byte>();
                m_stream.ToObservable(bufferSize, scheduler).Subscribe(m_read);
            }
            return m_read;
        }

        public IObservable<Unit> Write(Byte[] bytes)
        {
            return
            Observable.FromAsyncPattern<Byte[], int, int>(m_stream.BeginWrite, m_stream.EndWrite)
                (bytes, 0, bytes.Length);
        }

        public IObservable<Unit> Write(String text, Encoding encoding=null)
        {
            if(encoding== null)
            {
                encoding = Encoding.UTF8;
            }
            return Write(encoding.GetBytes(text));
        }

        public void Flush()
        {
            m_stream.Flush();
        }

#region IDisposable Support
        protected CompositeDisposable m_disposable = new CompositeDisposable();

        private bool disposedValue = false; // 重複する呼び出しを検出するには

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: マネージ状態を破棄します (マネージ オブジェクト)。
                    m_disposable.Dispose();
                }

                // TODO: アンマネージ リソース (アンマネージ オブジェクト) を解放し、下のファイナライザーをオーバーライドします。
                // TODO: 大きなフィールドを null に設定します。

                disposedValue = true;
            }
        }

        // TODO: 上の Dispose(bool disposing) にアンマネージ リソースを解放するコードが含まれる場合にのみ、ファイナライザーをオーバーライドします。
        // ~HttpMessageBase() {
        //   // このコードを変更しないでください。クリーンアップ コードを上の Dispose(bool disposing) に記述します。
        //   Dispose(false);
        // }

        // このコードは、破棄可能なパターンを正しく実装できるように追加されました。
        void IDisposable.Dispose()
        {
            // このコードを変更しないでください。クリーンアップ コードを上の Dispose(bool disposing) に記述します。
            Dispose(true);
            // TODO: 上のファイナライザーがオーバーライドされる場合は、次の行のコメントを解除してください。
            // GC.SuppressFinalize(this);
        }
#endregion
    }

    public class HttpsConnection: HttpConnection
    {
        public HttpsConnection(Uri uri):base(uri)
        {
        }

        protected override Stream GetStream(TcpClient connection)
        {
            var stream = base.GetStream(connection);
            var sslStream = new SslStream(stream, false, RemoteCertificateNoValidationCallback);
            //サーバーの認証を行う
            //これにより、RemoteCertificateValidationCallbackメソッドが呼ばれる
            sslStream.AuthenticateAsClient(m_uri.Host);

            return sslStream;
        }

        //サーバー証明書を検証するためのコールバックメソッド
        private static Boolean RemoteCertificateNoValidationCallback(Object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            //サーバー証明書を検証せずに無条件に許可する
            return true;
        }

        //サーバー証明書を検証するためのコールバックメソッド
        private static Boolean RemoteCertificateValidationCallback(Object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            //PrintCertificate(certificate);

            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                //Console.WriteLine("サーバー証明書の検証に成功しました\n");
                return true;
            }
            else
            {
                //何かサーバー証明書検証エラーが発生している

                //SslPolicyErrors列挙体には、Flags属性があるので、
                //エラーの原因が複数含まれているかもしれない。
                //そのため、&演算子で１つ１つエラーの原因を検出する。
                if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors) ==
                    SslPolicyErrors.RemoteCertificateChainErrors)
                {
                    throw new InvalidOperationException("ChainStatusが、空でない配列を返しました");
                }

                if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) ==
                    SslPolicyErrors.RemoteCertificateNameMismatch)
                {
                    throw new InvalidOperationException("証明書名が不一致です");
                }

                if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNotAvailable) ==
                    SslPolicyErrors.RemoteCertificateNotAvailable)
                {
                    throw new InvalidOperationException("証明書が利用できません");
                }

                //検証失敗とする
                return false;
            }
        }
    }
    #endregion

    #region HttpCookie
    public class HttpCookieItem
    {
        Uri m_uri;

        public String Key
        {
            get;
            private set;
        }

        public String Value
        {
            get;
            private set;
        }

        public HttpCookieItem(Uri uri, String key, String value
            , IEnumerable<KeyValuePair<String, String>> options)
        {
            m_uri = uri;
            Key = key;
            Value = value;
        }

        public override string ToString()
        {
            return String.Format("[{0}: \"{1}\"]", Key, Value); ;
        }
    }

    public class HttpCookieManager
    {
        List<HttpCookieItem> m_cookies = new List<HttpCookieItem>();

        public HttpCookieManager()
        {

        }

        public void Push(Uri uri, String cookieValue)
        {
            var splited =
            cookieValue
            .Split(';')
            .Select(x => x.Split(new[] { '=' }, 2))
            .Select(x => new KeyValuePair<String, String>(
                x.First().Trim()
                , x.Skip(1).First().Trim()))
            ;
            var kv = splited.First();
            var key = kv.Key;
            var lowerKey = key.ToLower();
            var value = kv.Value;
            if (value == "deleted")
            {
                while (true)
                {
                    var item = m_cookies.FirstOrDefault(x => x.Key.ToLower()==lowerKey);
                    if (item == null) break;
                    m_cookies.Remove(item);
                }                
            }
            else
            {
                var item =
                new HttpCookieItem(uri
                                    , key, value
                                    , splited.Skip(1));
                bool found = false;
                for(int i=0; i<m_cookies.Count; ++i)
                {
                    if(m_cookies[i].Key.ToLower()==lowerKey)
                    {
                        // 既存
                        found = true;
                        m_cookies[i] = item;
                    }
                }
                if (!found)
                {
                    // 無かった
                    m_cookies.Add(item);
                }
            }
        }

        public override string ToString()
        {
            return String.Join("\n", m_cookies.Select(x => x.ToString())) + "\n";
        }

        public String GetRequestValue()
        {
            return String.Join("; ", m_cookies.Select(x => x.Key + "=" + x.Value));
        }
    }
    #endregion

    #region HttpRequest
    public enum MethodType
    {
        GET,
        POST,
    }

    public class UrlForm: Dictionary<String, String>
    {

    }

    public class HttpRequest
    {
        public MethodType Method { get; set; }

        public Int32 Major { get; set; }
        public Int32 Minor { get; set; }

        public HttpRequest(Uri uri, HttpCookieManager cookie)
        {
            Major = 1;
            Minor = 1;
            Uri = uri;
            SetHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/42.0.2311.135 Safari/537.36 Edge/12.10240");
            SetHeader("Host", uri.Host);
            if(cookie!=null)
            {
                var cookievalue = cookie.GetRequestValue();
                if (!String.IsNullOrEmpty(cookievalue))
                {
                    SetHeader("Cookie", cookievalue);
                }
            }
        }

        public void EnableEncoding()
        {
            SetHeader("Accept-Encoding", "gzip, deflate");
        }

        public void SetKeepAlive(bool enable)
        {
            if (enable)
            {
                SetHeader("Connection", "keep-alive");
            }
            else
            {
                SetHeader("Connection", "close");
            }
        }

        public List<KeyValuePair<String, String>> Headers = new List<KeyValuePair<string, string>>();
        public void SetHeader(String key, String value)
        {
            var keyLower=key.ToLower();
            var header=Headers.Select((kv, i) => new { i, kv }).FirstOrDefault(x => x.kv.Key.ToLower() == keyLower);
            if (header != null)
            {
                // update
                Headers[header.i]= new KeyValuePair<string,string>(key, value);
            }
            else
            {
                // add
                Headers.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        public void RemoveHeader(String key)
        {
            var keyLower = key.ToLower();
            while (true)
            {
                var header = Headers.Select((kv, i) => new { i, kv }).FirstOrDefault(x => x.kv.Key.ToLower() == keyLower);
                if (header == null) break;
                Headers.RemoveAt(header.i);
            }
        }

        public Uri Uri { get; private set; }

        public Byte[] PostData { get; private set; }
        public void SetPostData(Byte[] postData)
        {
            Method = MethodType.POST;
            PostData = postData;
            if (postData != null && postData.Length > 0)
            {
                SetHeader("Content-Length", postData.Length.ToString());
                SetHeader("Content-Type", "application/x-www-form-urlencoded");
            }
        }
        public void SetPostData(UrlForm form)
        {
            var postText=
            String.Join("&"
            , form.Select(x => x.Key + "=" + x.Value));
            SetPostData(Encoding.ASCII.GetBytes(postText));
        }

        public String GetMethodline(MethodType method)
        {
            return String.Format("{0} {1} HTTP/{2}.{3}\r\n"
                , method
                , Uri.PathAndQuery
                , Major, Minor
                );
        }

        public override String ToString()
        {
            return GetMethodline(Method)
                + String.Join("", Headers.Select(x => String.Format("{0}: {1}\r\n", x.Key, x.Value)).ToArray())
                + "\r\n"
                ;
        }
    }
#endregion

    #region HttpResponseObserver
    class CRLFDetector
    {
        List<Byte> m_queue = new List<byte>() { default(Byte) };

        public bool IsHeaderEnd { get; private set; }

        public bool Detect(Byte current)
        {
            if (IsHeaderEnd) return false;

            var result = m_queue.Last() == 0x0d && current == 0x0a;
            if (result && m_queue.Count == 3)
            {
                if (m_queue[0] == 0x0d && m_queue[1] == 0x0a)
                {
                    // header end
                    IsHeaderEnd = true;
                }
            }
            m_queue.Add(current);
            if (m_queue.Count > 3)
            {
                m_queue.RemoveAt(0);
            }
            return result;
        }
    }

    public class ChunkAggregator
    {
        List<Byte> m_chunk = new List<Byte>();
        List<Byte> m_header = new List<Byte>();

        bool m_inChunkHeader = true;
        byte m_prev;
        int m_remain;

        public IList<Byte> Push(Byte x)
        {
            if (m_inChunkHeader)
            {
                m_header.Add(x);
                if (m_prev == 0x0d && x == 0x0a)
                {
                    // end of chunk header
                    var headline = Encoding.ASCII.GetString(m_header.Take(m_header.Count - 2).ToArray());
                    m_remain = Convert.ToInt32(headline, 16) + 2;
                    m_header.Clear();
                    m_inChunkHeader = false;
                }
                m_prev = x;
            }
            else
            {
                m_chunk.Add(x);
                --m_remain;
                if (m_remain == 0)
                {
                    // end ob chunk body
                    var chunk = m_chunk;
                    m_inChunkHeader = true;
                    m_chunk = new List<byte>();

                    // drop crlf
                    chunk.RemoveAt(chunk.Count - 1);
                    chunk.RemoveAt(chunk.Count - 1);

                    return chunk;
                }
            }

            return null;
        }
    }

    public static class TransferExtensions
    {
        public static IObservable<Byte[]> HttpChunk(
            this IObservable<Byte> source
            )
        {
            return Observable.Create<Byte[]>(observer =>
            {
                var chunkAggregator = new ChunkAggregator();
                return source.Subscribe(value =>
                {
                    var chunk = chunkAggregator.Push(value);
                    if (chunk!= null)
                    {
                        // chunk body が終わりに到達
                        observer.OnNext(chunk.ToArray());
                        if (chunk.Count == 0)
                        {
                            observer.OnCompleted();
                        }
                    }
                },
                observer.OnError,
                observer.OnCompleted);
            });
        }
    }

    public abstract class HttpResponseObserverBase : IObserver<HttpConnection>, IDisposable
    {
        CompositeDisposable m_disposable = new CompositeDisposable();

        IScheduler m_scheduler = Scheduler.CurrentThread;

        public IScheduler ReadScheduler
        {
            get { return m_scheduler; }
            set
            {
                m_scheduler = value;
            }
        }

        int m_readBufferSize=1024;
        public int ReadBufferSize
        {
            get { return m_readBufferSize; }
            set
            {
                m_readBufferSize = value;
            }
        }

        Subject<String> m_statusObservable = new Subject<string>();
        public IObservable<String> StatusObservable
        {
            get
            {
                return m_statusObservable;
            }
        }

        Subject<KeyValuePair<String, String>> m_headerObservable = new Subject<KeyValuePair<string, string>>();
        public IObservable<KeyValuePair<String, String>> HeaderObservable
        {
            get
            {
                return m_headerObservable;
            }
        }

        public virtual void OnCompleted()
        {
            // do nothing
        }

        public virtual void OnError(Exception error)
        {
            throw error;
        }

        public void OnNext(HttpConnection connect)
        {
            //m_disposable.Add(connect);

            var byteObservable = connect.Read(ReadBufferSize, ReadScheduler)
                ;

            var detector = new CRLFDetector();
            var lineObservable =
                byteObservable
                .BufferWhile(x => !detector.Detect(x))
                .Publish()
            ;

            // status
            var statusline = default(string);
            lineObservable.Take(1)
                .Select(x => Encoding.ASCII.GetString(x.ToArray()).TrimEnd())
                .Subscribe(x =>
                {
                    statusline = x;
                    m_statusObservable.OnNext(x);
                }
                , ex => m_statusObservable.OnError(ex)
                , () =>
                {
                    if (String.IsNullOrEmpty(statusline))
                    {
                        // error
                        m_statusObservable.OnError(new InvalidOperationException("no statusline"));
                    }
                    m_statusObservable.OnCompleted();
                }
                )
                .AddTo(m_disposable)
                ;

            // headers
            var headers = new List<KeyValuePair<String, String>>();
            lineObservable.Skip(1).TakeWhile(x => x.Count > 2 && !detector.IsHeaderEnd)
                .Select(x => Encoding.ASCII.GetString(x.ToArray()).TrimEnd())
                .Select(x => x.Split(new[] { ':' }, 2))
                .Select(x => new KeyValuePair<string, string>(x[0], x[1].TrimStart()))
                .Subscribe(m_headerObservable)
                .AddTo(m_disposable)
                ;

            // body
            InitializeByteObservable(byteObservable
                .SkipWhile(_ => !detector.IsHeaderEnd)
                )
                .AddTo(m_disposable)
                ;            

            lineObservable.Connect();
        }

        protected abstract IDisposable InitializeByteObservable(IObservable<Byte> byteObservable);

#region IDisposable Support
        private bool disposedValue = false; // 重複する呼び出しを検出するには

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: マネージ状態を破棄します (マネージ オブジェクト)。
                    m_disposable.Dispose();
                }

                // TODO: アンマネージ リソース (アンマネージ オブジェクト) を解放し、下のファイナライザーをオーバーライドします。
                // TODO: 大きなフィールドを null に設定します。

                disposedValue = true;
            }
        }

        // TODO: 上の Dispose(bool disposing) にアンマネージ リソースを解放するコードが含まれる場合にのみ、ファイナライザーをオーバーライドします。
        // ~HttpSubject() {
        //   // このコードを変更しないでください。クリーンアップ コードを上の Dispose(bool disposing) に記述します。
        //   Dispose(false);
        // }

        // このコードは、破棄可能なパターンを正しく実装できるように追加されました。
        public void Dispose()
        {
            // このコードを変更しないでください。クリーンアップ コードを上の Dispose(bool disposing) に記述します。
            Dispose(true);
            // TODO: 上のファイナライザーがオーバーライドされる場合は、次の行のコメントを解除してください。
            // GC.SuppressFinalize(this);
        }
#endregion
    }

    public class ByteStreamHttpResponseObserver : HttpResponseObserverBase
    {
        Subject<Byte> m_bodyObservable = new Subject<Byte>();
        public IObservable<Byte> BodyObservable
        {
            get
            {
                return m_bodyObservable;
            }
        }

        public override void OnError(Exception error)
        {
            m_bodyObservable.OnError(error);
        }

        protected override IDisposable InitializeByteObservable(IObservable<Byte> byteObservable)
        {
            return
            byteObservable            
            .Subscribe(m_bodyObservable)
            ;
        }
    }

    public class ChunkedHttpResponseObserver: HttpResponseObserverBase
    {
        Subject<Byte> m_bodyObservable = new Subject<Byte>();
        public IObservable<Byte> BodyObservable
        {
            get
            {
                return m_bodyObservable;
            }
        }

        Subject<Boolean> m_keepAliveObservable = new Subject<bool>();
        public IObservable<bool> KeepAliveObservalbe
        {
            get
            {
                return m_keepAliveObservable;
            }
        }

        public override void OnError(Exception error)
        {
            m_bodyObservable.OnError(error);
        }

        public static Byte[] Decode(Byte[] src, String encoding)
        {
            switch(encoding)
            {
                case "gzip":
                    using (var ms = new MemoryStream(src.ToArray()))
                    using (var ds = new GZipStream(ms, CompressionMode.Decompress))
                    {
                        var list = new List<Byte>();
                        int num;
                        var buf = new Byte[1024];
                        while ((num = ds.Read(buf, 0, buf.Length)) > 0)
                        {
                            list.AddRange(buf.Take(num));
                        }
                        return list.ToArray();
                    }

                case "deflate":
                    using (var ms = new MemoryStream(src.ToArray()))
                    using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                    {
                        var list = new List<Byte>();
                        int num;
                        var buf = new Byte[1024];
                        while ((num = ds.Read(buf, 0, buf.Length)) > 0)
                        {
                            list.AddRange(buf.Take(num));
                        }
                        return list.ToArray();
                    }
            }

            return src;
        }

        protected override IDisposable InitializeByteObservable(IObservable<Byte> byteObservable)
        {
            var disposable = new CompositeDisposable();

            int? contentLength = null;
            bool isChunked = false;
            string contentEncoding = null;
            bool keepAlive = false;

            var headers=new List<KeyValuePair<String, String>>();

            // status
            StatusObservable.Subscribe(x =>
            {

            }
            , ex => m_bodyObservable.OnError(ex)
            );

            // response headers
            HeaderObservable.Subscribe(x =>
            {
                headers.Add(x);
                switch (x.Key.ToLower())
                {
                    case "content-length":
                        contentLength = int.Parse(x.Value);
                        break;

                    case "transfer-encoding":
                        if (x.Value.ToLower() == "chunked")
                        {
                            isChunked = true;
                        }
                        break;

                    case "content-encoding":
                        contentEncoding = x.Value.ToLower();
                        break;

                    case "connection":
                        if (x.Value.ToLower() == "close")
                        {
                            keepAlive = false;
                        }
                        else if(x.Value.ToLower() == "keep-alive")
                        {
                            keepAlive = true;
                        }
                        break;
                }
            }
            , ex =>
            {
                m_bodyObservable.OnError(ex);
            }
            , () =>
            {
                m_keepAliveObservable.OnNext(keepAlive);

                // body
                if (contentLength.HasValue)
                {
                    if (contentLength.Value > 0)
                    {
                        var observable =
                            byteObservable
                                .Take(contentLength.Value);
                        // fixed body
                        if (String.IsNullOrEmpty(contentEncoding))
                        {
                            observable
                                .Subscribe(m_bodyObservable)
                                .AddTo(disposable)
                                ;
                        }
                        else
                        {
                            // Decode TransferEncoding
                            observable
                                .ToArray()
                                .Select(x => Decode(x, contentEncoding)).SelectMany(x => x)
                                .Subscribe(m_bodyObservable)
                                .AddTo(disposable)
                                ;
                        }
                    }
                    else
                    {
                        // empty body
                        Observable.Empty<byte>().Subscribe(m_bodyObservable)
                            .AddTo(disposable)
                            ;
                    }
                }
                else if (isChunked)
                {
                    // chunked body
                    byteObservable
                    .HttpChunk()
                    .SelectMany(x => x)
                    .Subscribe(m_bodyObservable)
                    .AddTo(disposable)
                    ;
                }
                else
                {
                    // no size body
                    if (String.IsNullOrEmpty(contentEncoding))
                    {
                        byteObservable
                            .Subscribe(m_bodyObservable)
                            .AddTo(disposable)
                            ;
                    }
                    else
                    {
                        // Decode TransferEncoding
                        byteObservable
                            .ToArray()
                            .Select(x => Decode(x, contentEncoding)).SelectMany(x => x)
                            .Subscribe(m_bodyObservable)
                            .AddTo(disposable)
                            ;
                    }
                }
            })
            .AddTo(disposable)
            ;

            return disposable;
        }
    }
    #endregion

    #region HttpResponse
    public class HttpResponse
    {
        public Uri Uri
        {
            get;
            private set;
        }

        public HttpCookieManager Cookie
        {
            get;
            private set;
        }

        public String Statusline
        {
            get;
            private set;
        }

        public Int32 StatusCode
        {
            get;
            private set;
        }

        public List<KeyValuePair<String, String>> Headers
        {
            get;
            private set;
        }

        public KeyValuePair<String, String>? GetFirstHeader(String key)
        {
            var lowerKey = key.ToLower();
            return Headers.FirstOrDefault(x => x.Key.ToLower() == lowerKey);
        }

        HttpResponse m_redirectFrom;
        public IEnumerable<HttpResponse> Redirects
        {
            get
            {
                if (m_redirectFrom!=null)
                {
                    yield return m_redirectFrom;
                    foreach(var r in m_redirectFrom.Redirects)
                    {
                        yield return r;
                    }
                }

            }
        }

        public IObservable<Byte> BodyObservable
        {
            get;
            private set;
        }

        public IObserver<Byte> CreateBodyObserver()
        {
            var subject = new Subject<Byte>();
            BodyObservable = subject;
            return subject;
        }

        public HttpResponse(Uri uri
            , HttpCookieManager cookie
            , String statusline
            , IEnumerable<KeyValuePair<String, String>> headers
            , HttpResponse redirectFrom=null
            )
        {
            Uri = uri;
            Cookie = cookie != null ? cookie : new HttpCookieManager();
            Statusline = statusline;
            StatusCode = int.Parse(statusline.Split().Skip(1).First());
            Headers = headers.ToList();
            // parser cookie
            foreach(var kv in headers)
            {
                if (kv.Key.ToLower() == "set-cookie")
                {
                    Cookie.Push(uri, kv.Value);
                }
            }
        }

        public override string ToString()
        {
            return String.Format("{0} => {1}\n{2}"
                , Uri, Statusline
                , String.Join("\n", Headers.Select(x => x.Key+"="+x.Value))
                );
        }
    }
    #endregion

    public static class HttpConnectionExtensions
    {
        public static IObservable<HttpResponse> ResponseObservable(this HttpConnection connection
            , HttpRequest request
            , HttpCookieManager cookie
            , HttpResponse redirectFrom=null
            )
        {
            // setup read
            var subject = new ChunkedHttpResponseObserver();
            subject.OnNext(connection);

            var observable=
            (
                from statusline in subject.StatusObservable
                from headers in subject.HeaderObservable.ToArray()
                select new HttpResponse(request.Uri
                        , cookie
                        , statusline, headers
                        , redirectFrom)
            )
            .Do(x =>
            {
                // connect body stream to response 
                subject.BodyObservable.Subscribe(x.CreateBodyObserver());
            })
            ;

            // SendRequest
            connection.SendRequestObservable(request).Subscribe(y =>
            {
            });

            return observable;
        }
    }
}
