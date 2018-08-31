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
                let read size (reader:IO.StreamReader) = 
                    async {
                        use b1 = Buffers.MemoryPool.Shared.Rent size
                        use b2 = Buffers.MemoryPool.Shared.Rent size
                        
                        let rec read b1 b2 (r:seq<_>) = 
                            async { 
                                let! reads = reader.ReadBlockAsync(b1, httpContext.RequestAborted).AsTask() |> Async.AwaitTask
                                let result = seq { yield! r; yield b1.Slice(0, reads) |> Memory.op_Implicit }
                                if reads > 0 then return! read b2 b1 result
                                else return result
                            }
                        
                        return! read b1.Memory b2.Memory Seq.empty
                    }
                        
                let write (writer:IO.StreamWriter) (input:ReadOnlyMemory<char> seq) = 
                    async {
                        for seg in input do
                            do! writer.WriteAsync(seg, httpContext.RequestAborted) |> Async.AwaitTask
                    }
                
                let tokenize (target:ReadOnlyMemory<_>) (dest:ReadOnlyMemory<_>) (x:ReadOnlyMemory<_> seq) : ReadOnlyMemory<_> seq = 
                    seq {
                        if target.Length = 0 then yield! x
                        else
                            use e = x.GetEnumerator()

                            let t1 = target.Slice(0,1)

                            let rec tokenize (current:ReadOnlyMemory<char>) = 
                                seq { 
                                    let pos = current.Span.IndexOf(target.Span)
                                    
                                    if pos < 0 then 
                                        if e.MoveNext () then 
                                            let pos1 = current.Span.LastIndexOf(t1.Span)
                                            if pos1 < 0 then 
                                                yield current
                                                yield! tokenize e.Current
                                            else 
                                                let h = current.Slice(pos1)
                                                if h.Length < target.Length then 
                                                    let t1 = target.Slice(0, h.Length)
                                                    let t2 = target.Slice(h.Length)
                                                    if h.Span.EndsWith(t1.Span) && e.Current.Span.StartsWith(t2.Span) then 
                                                        yield dest
                                                        yield! tokenize (e.Current.Slice(t2.Span.Length))
                                                    else
                                                        yield current
                                                        yield! tokenize e.Current
                                                else 
                                                    yield current
                                                    yield! tokenize e.Current 
                                        else yield current
                                    else
                                        if pos > 0 then yield current.Slice(0, pos)
                                        yield dest
                                        yield! tokenize (current.Slice(pos + target.Length))
                                }

                            if e.MoveNext() then yield! tokenize e.Current 
                    }

                let uri = UriHelper.GetEncodedUrl(httpContext.Request)
                let req = HttpWebRequest.CreateHttp uri
                
                req.AllowReadStreamBuffering <- false
                req.AllowWriteStreamBuffering <- false

                let! response = req.GetResponseAsync() |> Async.AwaitTask
                use streamResponse = new System.IO.StreamReader(response.GetResponseStream())
                use outStream = new System.IO.StreamWriter(httpContext.Response.Body)

                let target = MemoryExtensions.AsMemory "Domain"
                let dest = MemoryExtensions.AsMemory "Fuuuck"

////////////////////////////////////////////////////////////////////////                
                
                
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
                                                    do! current.Slice(0, pos1) |> write
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

                let target = MemoryExtensions.AsMemory "ve examples in docu"
                let dest   = MemoryExtensions.AsMemory "ve fuucking in docu"

                //do!
                //    streamResponse 
                //    |> read 16
                //    //|> Async.map (tokenize target dest)
                //    |> Async.bind (write outStream)
                
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
