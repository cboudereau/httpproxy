// Learn more about F# at http://fsharp.org

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open System.Net
open Microsoft.AspNetCore.Http.Extensions
open System

module Async = 
    let inline bind f x = async.Bind(x, f)
    let inline map f x = bind (f >> async.Return) x
    let inline apply f x = bind (fun f' -> map f' x) f
    let inline ret x = async.Return x

    module Operators = 
        let inline (<!>) f x = map f x
        let inline (>>=) f x = bind x f
        let inline (<*>) f x = apply f x

open Async.Operators

type Startup (env:IHostingEnvironment) = 
    member val public Configuration = (new ConfigurationBuilder()).AddEnvironmentVariables().Build() with get, set

    member public __.Configure(app:IApplicationBuilder, loggerFactory:ILoggerFactory) = 
        loggerFactory.AddConsole() |> ignore

        app.Run(fun httpContext -> 
            async {
                let forward size (target:ReadOnlyMemory<char>) (dest:ReadOnlyMemory<char>) (output:IO.StreamWriter) (input:IO.StreamReader) = 
                    async {
                        use b1 = Buffers.MemoryPool.Shared.Rent size
                        use b2 = Buffers.MemoryPool.Shared.Rent size

                        let read m = 
                            input.ReadBlockAsync(m, httpContext.RequestAborted).AsTask() 
                            |> Async.AwaitTask
                            |> Async.map (fun reads -> m.Slice(0, reads) |> Memory.op_Implicit)
                        
                        let write m = output.WriteAsync(m, httpContext.RequestAborted) |> Async.AwaitTask
                        
                        let t1 = target.Slice(0,1)

                        let rec forward m1 m2 (current:ReadOnlyMemory<char>) = 
                            async {
                                let forward = forward m2 m1
                                let read () = read m1
                                
                                if current.Length = 0 then
                                    let! next = read () 
                                    if next.Length > 0 then do! forward next
                                else
                                    let pos = current.Span.IndexOf(target.Span)
                                    if pos < 0 then 
                                        let pos1 = current.Span.LastIndexOf(t1.Span)
                                        let! next = read ()
                                        
                                        if next.Length = 0 then do! write current
                                        elif pos1 < 0 then 
                                            do! write current
                                            do! forward next
                                        else 
                                            let h = current.Slice(pos1)
                                            if h.Length < target.Length then
                                                let t1 = target.Slice(0, h.Length)
                                                let t2 = target.Slice(h.Length)
                                                if h.Span.EndsWith(t1.Span) && next.Span.StartsWith(t2.Span) then
                                                    do! write dest
                                                    do! next.Slice(t2.Length) |> forward
                                                else 
                                                    do! write current
                                                    do! forward next
                                            else 
                                                do! write current
                                                do! forward next
                                    else 
                                        if pos > 0 then do! current.Slice(0, pos) |> write
                                        do! write dest
                                        do! current.Slice (pos + target.Length) |> write
                            }
                        
                        do! forward b1.Memory b2.Memory ReadOnlyMemory.Empty
                    }

                let uri = UriHelper.GetEncodedUrl(httpContext.Request)
                let req = HttpWebRequest.CreateHttp uri
                
                req.AllowReadStreamBuffering <- false
                req.AllowWriteStreamBuffering <- false

                let! response = req.GetResponseAsync() |> Async.AwaitTask
                use streamResponse = new System.IO.StreamReader(response.GetResponseStream())
                use outStream = new System.IO.StreamWriter(httpContext.Response.Body)

                let target = MemoryExtensions.AsMemory "this domain in examples without prior coordination or asking for permission"
                let dest   = MemoryExtensions.AsMemory "this fuuuck in examples without prior coordination or asking for permission"

                do! streamResponse |> forward 16 target dest outStream

            } |> Async.StartAsTask :> System.Threading.Tasks.Task
        )

[<EntryPoint>]
let main argv =
    
    let config = (ConfigurationBuilder()).Build()

    let builder = 
        (new WebHostBuilder()).UseConfiguration(config).UseStartup<Startup>()
            .UseKestrel()
            .UseUrls("http://localhost:5000");

    let host = builder.Build();
    host.Run();

    0 // return an integer exit code
