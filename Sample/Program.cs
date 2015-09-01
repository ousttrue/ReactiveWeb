#define USE_BYTES_BODY

using ReactiveWeb;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Sample
{
    class HttpManager
    {
        HttpConnection m_connection;

        IObservable<HttpConnection> GetConnection(Uri uri)
        {
            if(m_connection!= null 
                && m_connection.Uri.Host==uri.Host 
                && m_connection.Uri.Port==uri.Port
                && m_connection.IsConnected
                )
            {
                return Observable.Return(m_connection)
                    .Do(x =>
                    {
                        Console.WriteLine("<KeepAlive>");
                    })
                    ;
            }
            else
            {
                return HttpConnection.Create(uri).Connect()
                    .Do(x => {
                        m_connection = x;
                        Console.WriteLine(x);
                    })
                    ;
            }
        }

        void Close()
        {
            if(m_connection!=null && m_connection.IsConnected)
            {
                (m_connection as IDisposable).Dispose();
                m_connection = null;
            }
        }

        public void Request(Uri uri)
        {
            Console.WriteLine("############################################################");

            // request
            var request = new HttpRequest(uri);
            request.EnableEncoding();
            request.SetKeepAlive(true);
            Console.Write(request.ToString());

#if USE_BYTES_BODY
            using (var subject = new ChunkedHttpResponseObserver())
            {
                var body = new Subject<IList<Byte>>();
                body.Subscribe(x =>
                {
                    Console.WriteLine("body: " + x.Count + " bytes");
                }
                , ex =>
                {
                    Console.WriteLine(ex);
                }
                , () =>
                {
                    Console.WriteLine("body end");
                });
                subject.BodyObservable.Subscribe(body);

                bool keepAlive = true;
                subject.KeepAliveObservalbe
                    .Subscribe(x =>
                    {
                        // disconnect
                        keepAlive = x;
                    });
#else
            using (var subject = new HttpRawByteSubject())
            {
                var body = new Subject<Byte>();
                var buffer = new List<Byte>();
                body.Subscribe(x =>
                {
                    buffer.Add(x);
                }
                , ex =>
                {
                    Console.WriteLine(ex);
                }
                , () =>
                {
                    Console.WriteLine("body: " + buffer.Count + " bytes");
                    Console.WriteLine("body end");
                });
                subject.BodyObservable.Subscribe(body);
#endif

                // statusline
                subject.StatusObservable.Subscribe(x =>
                {
                    Console.WriteLine("status: " + x);
                });

                // response headers
                subject.HeaderObservable.Subscribe(x =>
                {
                    Console.WriteLine(x.Key + ": " + x.Value);
                }
                , () =>
                {
                    Console.WriteLine();
                }
                );

                ////////////////////////////
                // connect
                ////////////////////////////
                GetConnection(uri)
                    .Subscribe(x =>
                    {
                        // ready to read
                        subject.OnNext(x);

                        // send request
                        x.SendRequestObservable(request)
                        .Subscribe(_ =>
                        {

                        });
                    }
                    , subject.OnError
                    )
                    ;

                // wait...
                body.Wait();

                if (!keepAlive)
                {
                    Close();
                }
            }
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            var manager = new HttpManager();

            foreach (var arg in args)
            {
                manager.Request(new Uri(arg));
            }

            // hit enter key
            Console.ReadLine();
        }
    }
}
