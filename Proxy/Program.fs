// Learn more about F# at http://fsharp.org

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open System.Net
open Microsoft.AspNetCore.Http.Extensions
open System

module Seq = 
    let inline add x source = seq { yield! source; yield x }

module ReadOnlyMemory = 
    let inline length (x:ReadOnlyMemory<_>) = x.Length
    let inline isEmpty x = length x = 0
    let inline span (x:ReadOnlyMemory<_>) = x.Span
    let inline indexOf target x = (span x).IndexOf(span target)

module Async = 
    let inline bind f x = async.Bind(x, f)
    let inline map f x = bind (f >> async.Return) x
    let inline apply f x = bind (fun f' -> map f' x) f
    let inline ret x = async.Return x
    let inline iter f x = async.For(x,f)

    module Operators = 
        let inline (<!>) f x = map f x
        let inline (>>=) x f = bind f x
        let inline (<*>) f x = apply f x

open Async.Operators

module NonContiguousMemory = 
    open System

    let replace (target:ReadOnlyMemory<_>) (dest:ReadOnlyMemory<_>) (source:seq<_>) = 
        seq {
            use e = source.GetEnumerator()
            let t1 = target.Slice(0,1)

            let rec replace target dest current = 
                seq {
                    let pos = current |> ReadOnlyMemory.indexOf target

                    if pos < 0 then
                        if e.MoveNext () then
                            let pos1 = current |> ReadOnlyMemory.indexOf t1
                            let next = e.Current

                            if pos1 < 0 then 
                                yield current
                                yield! replace target dest next
                            else
                                let l = current.Length - pos1
                                if l < target.Length then
                                    let t1 = target.Slice(0, l)
                                    let t2 = target.Slice(l)
                                    if current.Span.EndsWith(t1.Span) && next.Span.StartsWith(t2.Span) then
                                        if pos1 > 0 then yield current.Slice(0, pos1)
                                        yield dest
                                        yield! next.Slice(t2.Length) |> replace target dest
                                    else
                                        yield current
                                        yield! replace target dest next
                                else
                                    yield current
                                    yield! replace target dest next
                        else yield current
                    else 
                        if pos > 0 then yield current.Slice(0, pos)
                        yield dest
                        yield! current.Slice(pos + target.Length) |> replace target dest
                }
            
            if e.MoveNext () then yield! replace target dest e.Current
        }

module Proxy = 
    type [<Struct>] Reader<'a> = Reader of (ReadOnlyMemory<'a> seq -> ReadOnlyMemory<'a> seq)

    let run size flush (Reader f) read = 
        async {
            use b1 = Buffers.MemoryPool.Shared.Rent size
            use b2 = Buffers.MemoryPool.Shared.Rent size

            let flush x = async { if ReadOnlyMemory.isEmpty x |> not then do! flush x }

            let rec run slot1 slot2 length current = 
                async {
                    if current |> Seq.isEmpty then
                        let! next = read slot1
                        do! Seq.singleton next |> run slot1 slot2 (ReadOnlyMemory.length next)
                    else
                        let r = f current
                        
                        if length = size then
                            let! last = r |> Seq.fold (fun s x -> (fun () -> x) <!> (s >>= flush)) (Async.ret ReadOnlyMemory.Empty)
                            
                            let! next = read slot2
                            do! 
                                if last.Length > 0 then [last;next] else [next]
                                |> run slot2 slot1 next.Length
                        else do! r |> Async.iter flush
                }
            
            do! run b1.Memory b2.Memory 0 List.empty
        }

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
                                if current.Length = 0 then
                                    let! next = read m1 
                                    if next.Length > 0 then do! forward m1 m2 next
                                else
                                    let pos = current.Span.IndexOf(target.Span)
                                    if pos < 0 then 
                                        let pos1 = current.Span.LastIndexOf(t1.Span)
                                        let! next = read m2
                                        
                                        if next.Length = 0 then do! write current
                                        elif pos1 < 0 then 
                                            do! write current
                                            do! forward m2 m1 next
                                        else 
                                            if current.Length - pos1 < target.Length then
                                                let h = current.Slice(pos1)
                                                let t1 = target.Slice(0, h.Length)
                                                let t2 = target.Slice(h.Length)
                                                if h.Span.EndsWith(t1.Span) && next.Span.StartsWith(t2.Span) then
                                                    if pos1 > 0 then do! current.Slice(0, pos1) |> write
                                                    do! write dest
                                                    do! next.Slice(t2.Length) |> forward m2 m1
                                                else 
                                                    do! write current
                                                    do! forward m2 m1 next
                                            else 
                                                do! write current
                                                do! forward m2 m1 next
                                    else 
                                        if pos > 0 then do! current.Slice(0, pos) |> write
                                        do! write dest
                                        do! current.Slice (pos + target.Length) |> forward m1 m2
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

                let replace = 
                    let replace target dest = 
                        let t = MemoryExtensions.AsMemory target
                        let d = MemoryExtensions.AsMemory dest
                        NonContiguousMemory.replace t d

                    replace "ve examples in docu" "ve fuucking in docu" >> replace "domain" "fuuuck"

                //do! streamResponse |> forward 16 target dest outStream
                do! Proxy.run 16 (fun x -> outStream.WriteAsync(x) |> Async.AwaitTask) (Proxy.Reader replace) (fun m -> streamResponse.ReadBlockAsync(m).AsTask() |> Async.AwaitTask |> Async.map (fun r -> m.Slice(0,r) |> Memory.op_Implicit))

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
