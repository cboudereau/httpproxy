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

module NonContiguousMemory = 
    let replace2 (target:ReadOnlyMemory<_>) (dest:ReadOnlyMemory<_>) source = 
        let t1 = target.Slice(0,1)
        
        let rec replace (state:ReadOnlyMemory<_> seq) (left:ReadOnlyMemory<_>) (right:ReadOnlyMemory<_>): struct (ReadOnlyMemory<_> * ReadOnlyMemory<_> list) = 
            let targetS = target.Span
            
            if right.Length = 0 then struct (left, state |> Seq.toList)
            elif left.Length = 0 then
                let pos = right.Span.IndexOf(targetS)
                if pos < 0 then replace state right ReadOnlyMemory.Empty
                else 
                    let state = if pos > 0 then seq { yield right.Slice(0, pos); yield dest } |> Seq.append state else state |> Seq.add dest
                    replace state left (right.Slice (pos + target.Length))
            else 
                let leftS = left.Span
                let pos1 = leftS.LastIndexOf(t1.Span)
                if pos1 < 0 then 
                    replace (state |> Seq.add left) ReadOnlyMemory.Empty right
                else
                    let l = leftS.Length - pos1 
                    if l < target.Length then
                        let t1 = target.Slice(0, l)
                        let t2 = target.Slice(l)
                        if leftS.EndsWith(t1.Span) && right.Span.StartsWith(t2.Span) then
                            let state = if pos1 > 0 then seq { yield left.Slice(0, pos1); yield dest } |> Seq.append state else state |> Seq.add dest
                            replace state ReadOnlyMemory.Empty (right.Slice(t2.Length))
                        else replace (state |> Seq.add left) ReadOnlyMemory.Empty right
                    else replace (state |> Seq.add left) ReadOnlyMemory.Empty right

                        
        source 
        |> List.fold (fun struct (remaining, result) current -> let struct (rest,res) = replace Seq.empty remaining current in struct (rest, Seq.append result res)) (struct (ReadOnlyMemory.Empty, Seq.empty))
        |> fun struct (rest, state) -> state |> Seq.add rest |> Seq.toList

    let replace (target:ReadOnlyMemory<_>) (dest:ReadOnlyMemory<_>) (source:#seq<_>) = 
        seq {
            use e = source.GetEnumerator()

            let rec replace (target:ReadOnlyMemory<_>) (dest:ReadOnlyMemory<_>) (current:ReadOnlyMemory<_>) = 
                seq {
                    let t1 = target.Slice(0,1)
                    let c = current.Span
                    let t = target.Span
                    let pos = c.IndexOf(t)

                    if pos < 0 && e.MoveNext() then
                        let pos1 = c.LastIndexOf(t1.Span)
                        let next = e.Current

                        if pos1 < 0 then 
                            yield current
                            yield! replace target dest next
                        else
                            let l = c.Length - pos1
                            if l < target.Length then
                                let t1 = target.Slice(0, l)
                                let t2 = target.Slice(l)
                                if c.EndsWith(t1.Span) && next.Span.StartsWith(t2.Span) then
                                    if pos1 > 0 then yield current.Slice(0, pos1)
                                    yield dest
                                    yield! next.Slice(t2.Length) |> replace target dest
                                else
                                    yield current
                                    yield! replace target dest next
                            else
                                yield current
                                yield! replace target dest next
                    else 
                        if pos > 0 then yield current.Slice(0, pos)
                        yield dest
                        yield! current.Slice(pos + target.Length) |> replace target dest
                }
            
            if e.MoveNext () then yield! replace target dest e.Current
        } |> Seq.toList

module Proxy = 
    type [<Struct>] Reader<'a> = Reader of (ReadOnlyMemory<'a> list -> ReadOnlyMemory<'a> list)

    let run size flush (Reader f) read = 
        async {
            use b1 = Buffers.MemoryPool.Shared.Rent size
            use b2 = Buffers.MemoryPool.Shared.Rent size

            let flush (x:ReadOnlyMemory<_>) = async { if x.Length > 0 then do! flush x }

            let rec run slot1 slot2 length current = 
                async {
                    if current |> List.isEmpty then
                        let! (next:ReadOnlyMemory<_>) = read slot1
                        do! List.singleton next |> run slot1 slot2 next.Length
                    else
                        let r = f current
                        
                        if length = size then
                            if r |> List.isEmpty then
                                let! next = read slot2
                                do! run slot2 slot1 next.Length [next]
                            else
                                let treated = 
                                    let count = r.Length - 1
                                    if count > 0 then r |> List.take count else List.empty
                                do! async.For(treated, flush)
                                let last = r |> List.last
                                let! next = read slot2
                                do! run slot2 slot1 next.Length [last;next]
                        else do! async.For(r, flush)
                }
            
            do! run b1.Memory b2.Memory 0 List.empty
        }

//let l = [ "hel"; "lo" ] |> List.map MemoryExtensions.AsMemory

//let e = (l |> List.toSeq).GetEnumerator()

//let r2 r = r

//let v = r2 e.Current


//let r = NonContiguousMemory.replace (MemoryExtensions.AsMemory "hello") (MemoryExtensions.AsMemory "world") l

//printfn "%A" r

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

                //let target = MemoryExtensions.AsMemory "domain"
                //let dest   = MemoryExtensions.AsMemory "fuuuck"
                let target = MemoryExtensions.AsMemory "ve examples in docu"
                let dest   = MemoryExtensions.AsMemory "ve fuucking in docu"
                
                //do! streamResponse |> forward 16 target dest outStream
                do! Proxy.run 16 (fun x -> outStream.WriteAsync(x) |> Async.AwaitTask) (NonContiguousMemory.replace2 target dest |> Proxy.Reader) (fun m -> streamResponse.ReadBlockAsync(m).AsTask() |> Async.AwaitTask |> Async.map (fun r -> m.Slice(0,r) |> Memory.op_Implicit))

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
