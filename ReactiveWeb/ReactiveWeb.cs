﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ReactiveWeb
{
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
        public static IObservable<IList<TSource>> BufferWhile<TSource>(
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
                var initialState = new StreamReaderState(source, buffersize);
                var currentStateSubscription = new SerialDisposable();
                Action<StreamReaderState, Action<StreamReaderState>> iterator =
                (state, self) =>
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
                        self(state);
                    }
                    else
                    {
                        o.OnCompleted();
                    }
                },
                o.OnError);
                var scheduledWork = scheduler.Schedule(initialState, iterator);
                return new CompositeDisposable(currentStateSubscription, scheduledWork);
            });
            return Observable.Using(() => source, _ => bytes);
        }
    }

    public class HttpConnection : IDisposable
    {
        Stream m_stream;

        public HttpConnection(Uri uri, TcpClient connection)
        {
            m_disposable.Add(connection);
            var stream = GetStream(uri, connection);
            m_stream = stream;
            m_disposable.Add(m_stream);

            Status = String.Format("connected from {0} to {1}"
                , connection.Client.LocalEndPoint
                , connection.Client.RemoteEndPoint
                );
        }

        protected virtual Stream GetStream(Uri _, TcpClient connection)
        {
            return connection.GetStream();
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

        public IObservable<Byte> Read(int bufferSize=1024, IScheduler scheduler=null)
        {
            return m_stream.ToObservable(bufferSize, scheduler!=null ? scheduler : NewThreadScheduler.Default);
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
        public HttpsConnection(Uri uri, TcpClient connection):base(uri, connection)
        {
        }

        protected override Stream GetStream(Uri uri, TcpClient connection)
        {
            var stream = connection.GetStream();
            var sslStream = new SslStream(stream, false, RemoteCertificateNoValidationCallback);
            //サーバーの認証を行う
            //これにより、RemoteCertificateValidationCallbackメソッドが呼ばれる
            sslStream.AuthenticateAsClient(uri.Host);

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

    public enum MethodType
    {
        GET,
        POST,
    }

    public class HttpRequest
    {
        public MethodType Method { get; set; }

        public Int32 Major { get; set; }
        public Int32 Minor { get; set; }

        public HttpRequest(Uri uri)
        {
            Major = 1;
            Minor = 1;
            Uri = uri;
            SetHeader("Host", uri.Host);
#if false
            SetHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/42.0.2311.135 Safari/537.36 Edge/12.10240");
            SetHeader("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            //SetHeader("Accept-Encoding", "gzip, deflate");
            SetHeader("Accept-Language", "ja,en-US;q=0.8,en;q=0.6");
#endif
        }

        public List<KeyValuePair<String, String>> Headers = new List<KeyValuePair<string, string>>();
        public void SetHeader(String key, String value)
        {
            var _key=key.ToLower();
            var header=Headers.Select((kv, i) => new { i, kv }).FirstOrDefault(x => x.kv.Key.ToLower() == _key);
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

        public IObservable<HttpConnection> Connect()
        {
            var client = new TcpClient();
            return 
                from _ in Observable.FromAsyncPattern<string, int>(
                client.BeginConnect, client.EndConnect)(Uri.Host, Uri.Port)
                let connection = (Uri.Scheme == "https") 
                ? new HttpsConnection(Uri, client)
                : new HttpConnection(Uri, client)
                from wait_for_write_request_header in connection.Write(ToString())
                from wait_for_write_request_body in ((Method == MethodType.POST && PostData != null && PostData.Length > 0)
                ? connection.Write(PostData)
                : Observable.Return(Unit.Default))
                select connection;
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
                + String.Join("", Headers.Select(x => String.Format("{0}: {1}\r\n", x.Key, x.Value)))
                + "\r\n"
                ;
        }
    }

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
        public static IObservable<IList<Byte>> HttpChunk(
            this IObservable<Byte> source
            )
        {
            return Observable.Create<IList<Byte>>(observer =>
            {
                var chunkAggregator = new ChunkAggregator();
                return source.Subscribe(value =>
                {
                    var chunk = chunkAggregator.Push(value);
                    if (chunk!= null)
                    {
                        // chunk body が終わりに到達
                        observer.OnNext(chunk);
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

    public abstract class HttpSubjectBase : IObserver<HttpConnection>, IDisposable
    {
        CompositeDisposable m_disposable = new CompositeDisposable();

        IScheduler m_scheduler;
        Action<Exception> m_errorHandler;
        int m_readBufferSize;

        public HttpSubjectBase(int readBufferSize = 1024, IScheduler scheduler = null, Action<Exception> errorHandler = null)
        {
            m_readBufferSize = readBufferSize;
            m_scheduler = scheduler;
            m_errorHandler = errorHandler;
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
            if (m_errorHandler != null)
            {
                m_errorHandler(error);
            }
            else
            {
                throw error;
            }
        }

        public void OnNext(HttpConnection connect)
        {
            m_disposable.Add(connect);

            var byteObservable = connect.Read(m_readBufferSize, m_scheduler)
                .Publish()
                ;

            var detector = new CRLFDetector();
            var lineObservable =
                byteObservable
                .BufferWhile(x => !detector.Detect(x))
                .Publish()
            ;

            // status
            lineObservable.Take(1)
                .Select(x => Encoding.ASCII.GetString(x.ToArray()).TrimEnd())
                .Subscribe(m_statusObservable)
                .AddTo(m_disposable)
                ;

            // headers
            var headers = new List<KeyValuePair<String, String>>();
            lineObservable.Skip(1).TakeWhile(x => x.Count > 2)
                .Select(x => Encoding.ASCII.GetString(x.ToArray()).TrimEnd())
                .Select(x => x.Split(new[] { ':' }, 2))
                .Select(x => new KeyValuePair<string, string>(x[0], x[1].TrimStart()))
                .Subscribe(m_headerObservable)
                .AddTo(m_disposable)
                ;

            // body
            InitializeByteObservable(byteObservable.SkipWhile(_ => !detector.IsHeaderEnd))
                .AddTo(m_disposable)
                ;            

            lineObservable.Connect();
            byteObservable.Connect();
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

    public class HttpRawByteSubject : HttpSubjectBase
    {
        Subject<Byte> m_bodyObservable = new Subject<Byte>();
        public IObservable<Byte> BodyObservable
        {
            get
            {
                return m_bodyObservable;
            }
        }

        protected override IDisposable InitializeByteObservable(IObservable<Byte> byteObservable)
        {
            return
            byteObservable            
            .Subscribe(m_bodyObservable)
            ;
        }
    }

    public class HttpChunkBytesSubject: HttpSubjectBase
    {
        Subject<IList<Byte>> m_bodyObservable = new Subject<IList<Byte>>();
        public IObservable<IList<Byte>> BodyObservable
        {
            get
            {
                return m_bodyObservable;
            }
        }

        protected override IDisposable InitializeByteObservable(IObservable<Byte> byteObservable)
        {
            var disposable = new CompositeDisposable();

            int? contentLength = null;
            bool isChunked = false;

            // response headers
            HeaderObservable.Subscribe(x =>
            {
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

                }
            }
            , () =>
            {
                // body
                if (contentLength.HasValue)
                {
                    if (contentLength.Value > 0)
                    {
                        // fixsized body
                        byteObservable
                        .Buffer(contentLength.Value)
                        .Take(1)
                        .Subscribe(m_bodyObservable)
                        .AddTo(disposable)
                        ;
                    }
                    else
                    {
                        Observable.Return(new List<Byte>()).Subscribe(m_bodyObservable)
                            .AddTo(disposable)
                            ;
                    }
                }
                else if (isChunked)
                {
                    // chunked body
                    byteObservable
                    .HttpChunk()
                    .Subscribe(m_bodyObservable)
                    .AddTo(disposable)
                    ;
                }
                else
                {
                    // no size body
                    var body = new List<Byte>();
                    byteObservable.Subscribe(x =>
                    {
                        body.Add(x);
                    }
                    , m_bodyObservable.OnError
                    , () =>
                    {
                        m_bodyObservable.OnNext(body);
                        m_bodyObservable.OnCompleted();
                    })
                    .AddTo(disposable)
                    ;
                }
            })
            .AddTo(disposable)
            ;

            return disposable;
        }
    }
}
