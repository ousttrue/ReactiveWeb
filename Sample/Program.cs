using ReactiveWeb;
using System;
using System.IO;
using System.Reactive.Linq;

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

            using (var subject = new HttpSubject())
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
                });

                // response body
                var ms = new MemoryStream();
                subject.BodyObservable.Subscribe(x =>
                {
                    ms.WriteByte(x);
                }
                , ex=>Console.WriteLine(ex)
                , ()=>
                {
                    Console.WriteLine();
                    Console.WriteLine(ms.ToArray().Length + " bytes");
                });

                // execute
                var cancel =
                request.Connect().Subscribe(subject);

                // wait...
                Console.ReadLine();
            }
        }
    }
}
