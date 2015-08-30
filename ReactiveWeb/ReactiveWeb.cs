﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
                throw new ArgumentNullException(nameof(register));
            return register(resource);
        }
        public static void AddTo<T>(this T resource, CompositeDisposable register) where T : IDisposable
        {
            if (register == null)
                throw new ArgumentNullException(nameof(register));
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

        public HttpConnection(TcpClient connection)
        {
            m_disposable.Add(connection);
            m_stream = connection.GetStream();
            m_disposable.Add(m_stream);
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

    public enum MethodType
    {
        GET,
        POST,
    }

    public class HttpRequest
    {
        public List<KeyValuePair<String, String>> Headers = new List<KeyValuePair<string, string>>();

        public Uri Uri { get; private set; }

        public HttpRequest(Uri uri)
        {
            Uri = uri;
            Headers.Add(new KeyValuePair<String, String>("Host", uri.Host));
        }

        public IObservable<HttpConnection> Connect()
        {
            var client = new TcpClient();
            return Observable.FromAsyncPattern<string, int>(
                client.BeginConnect, client.EndConnect)(Uri.Host, Uri.Port)
                .Select(_ => new HttpConnection(client))
                .Do(x =>
                {
                    x.Write(ToString(MethodType.GET));
                })
                ;
        }

        public String GetHeadline(MethodType method)
        {
            return String.Format("{0} {1} HTTP/1.1\r\n"
                , method
                , Uri.PathAndQuery
                );
        }

        public String ToString(MethodType method)
        {
            return GetHeadline(method)
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

    public class HttpSubject : IObserver<HttpConnection>, IDisposable
    {
        CompositeDisposable m_disposable = new CompositeDisposable();

        IScheduler m_scheduler;
        Action<Exception> m_errorHandler;
        int m_readBufferSize;

        public HttpSubject(int readBufferSize=1024, IScheduler scheduler=null, Action<Exception> errorHandler=null)
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

        Subject<Byte> m_bodyObservable = new Subject<Byte>();
        public IObservable<Byte> BodyObservable
        {
            get
            {
                return m_bodyObservable;
            }
        }

        public void OnCompleted()
        {
            // do nothing
        }

        public void OnError(Exception error)
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

            lineObservable.Take(1)
                .Select(x => Encoding.ASCII.GetString(x.ToArray()).TrimEnd())
                .Subscribe(m_statusObservable)
                .AddTo(m_disposable)
                ;

            var headers = new List<KeyValuePair<String, String>>();
            lineObservable.Skip(1).TakeWhile(x => x.Count > 2)
                .Select(x => Encoding.ASCII.GetString(x.ToArray()).TrimEnd())
                .Select(x => x.Split(new[] { ':' }, 2))
                .Select(x => new KeyValuePair<string, string>(x[0], x[1].TrimStart()))
                .Subscribe(m_headerObservable)
                .AddTo(m_disposable)
                ;

            byteObservable.SkipWhile(_ => !detector.IsHeaderEnd)
            .Subscribe(m_bodyObservable)
            .AddTo(m_disposable)
            ;

            lineObservable.Connect();
            byteObservable.Connect();
        }

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
}
