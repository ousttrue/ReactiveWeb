using ReactiveWeb;
using System;
using System.Collections.Generic;
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

                int? contentLength = null;
                bool isChunked = false;

                // response headers
                subject.HeaderObservable.Subscribe(x =>
                {
                    Console.WriteLine(x.Key + ": " + x.Value);
                    switch(x.Key.ToLower())
                    {
                        case "content-length":
                            contentLength = int.Parse(x.Value);
                            break;

                        case "transfer-encoding":
                            if (x.Value.ToLower()=="chunked")
                            {
                                isChunked = true;
                            }
                            break;

                    }
                }
                , ()=>
                {
                    // body
                    if (contentLength.HasValue)
                    {
                        // fixsized body
                        Console.WriteLine();
                        subject.BodyObservable.Buffer(contentLength.Value).Subscribe(x =>
                        {
                            Console.WriteLine("fixed: " + x.Count + " bytes");
                        });
                    }
                    else if(isChunked)
                    {
                        // chunked body
                        Console.WriteLine();
                        subject.BodyObservable.HttpChunk().Subscribe(x =>
                        {
                            Console.WriteLine("chunk: " + x.Count + " bytes");
                        });
                    }
                    else
                    {
                        // no size body
                        Console.WriteLine();
                        var body = new List<Byte>();
                        subject.BodyObservable.Subscribe(x =>
                        {
                            body.Add(x);
                        }
                        , ex => Console.WriteLine(ex)
                        , () =>
                        {
                            Console.WriteLine(body.ToArray().Length + " bytes");
                        });
                    }
                });

                // execute
                var cancel =
                request.Connect().Subscribe(subject);

                // wait...
                subject.BodyObservable.Wait();
            }

            // hit enter key
            Console.ReadLine();
        }
    }
}
