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
            Console.Write(request.ToString());

#if USE_BYTES_BODY
            using (var subject = new HttpChunkBytesSubject())
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

                // execute
                var connectObservable = request.Connect().Publish();

                connectObservable.Subscribe(x =>
                {
                    Console.WriteLine(x);
                });

                var cancel = connectObservable
                .Subscribe(subject);

                connectObservable.Connect();

                // wait...
                body.Wait();
            }

            // hit enter key
            Console.ReadLine();
        }
    }
}
