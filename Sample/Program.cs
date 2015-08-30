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
    class Program
    {
        static void Main(string[] args)
        {
            // request
            var uri = new Uri(args[0]);
            var request = new HttpRequest(uri);
            Console.Write(request.ToString(MethodType.GET));

#if USE_BYTES_BODY
            using (var subject = new HttpChunkBytesSubject())
#else
            using (var subject = new HttpRawByteSubject())
#endif
            {
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

                // body
#if USE_BYTES_BODY
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
#else
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
#endif
                subject.BodyObservable.Subscribe(body);

                // execute
                var cancel =
                request.Connect().Subscribe(subject);

                // wait...
                body.Wait();
            }

            // hit enter key
            Console.ReadLine();
        }
    }
}
