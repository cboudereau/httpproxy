// Learn more about F# at http://fsharp.org

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open System.Net
open Microsoft.AspNetCore.Http.Extensions
open System

//https://docs.oracle.com/cd/E23095_01/Search.93/ATGSearchAdmin/html/s1207adjustingtcpsettingsforheavyload01.html
//"System.Net.WebException": Only one usage of each socket address (protocol/network address/port) is normally permitted
//System.Net.ServicePointManager.DefaultConnectionLimit <- 50

//let httpClient = new System.Net.Http.HttpClient()

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

type Static (env:IHostingEnvironment) = 
    member public __.Configure(app:IApplicationBuilder, loggerFactory:ILoggerFactory) = 
        //loggerFactory.AddConsole() |> ignore
        app.UseStaticFiles().UseDirectoryBrowser().UseDefaultFiles().UseFileServer()
        |> ignore

type Identity (env:IHostingEnvironment) = 
    member public __.Configure(app:IApplicationBuilder, loggerFactory:ILoggerFactory) = 
        loggerFactory.AddConsole() |> ignore
        app.Run(fun httpContext -> 
            async {
                httpContext.Response.ContentType <- httpContext.Request.ContentType
                if httpContext.Request.Headers.ContainsKey("Accept-Encoding") 
                    && httpContext.Request.Headers.["Accept-Encoding"].Item 0 = "gzip" then
                    httpContext.Response.Headers.Add("Content-Encoding", Microsoft.Extensions.Primitives.StringValues("gzip"))
                    if httpContext.Request.Headers.ContainsKey("Content-Encoding") |> not then
                        use ms = new System.IO.MemoryStream()

                        let output = 
                            new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, true)
                            :> System.IO.Stream

                        do! httpContext.Request.Body.CopyToAsync(output) |> Async.AwaitTask
                        output.Dispose()
                        httpContext.Response.ContentLength <- Nullable(ms.Length)
                        ms.Position <- 0L
                        do! ms.CopyToAsync(httpContext.Response.Body) |> Async.AwaitTask
                    else
                        httpContext.Response.ContentLength <- httpContext.Request.ContentLength
                        do! httpContext.Request.Body.CopyToAsync(httpContext.Response.Body) |> Async.AwaitTask
                else 
                    httpContext.Response.ContentLength <- httpContext.Request.ContentLength
                    do! httpContext.Request.Body.CopyToAsync(httpContext.Response.Body) |> Async.AwaitTask
            } |> Async.StartAsTask :> System.Threading.Tasks.Task)
        |> ignore

type Forward (env:IHostingEnvironment) = 
    member val public Configuration = (new ConfigurationBuilder()).AddEnvironmentVariables().Build() with get, set

    member public __.Configure(app:IApplicationBuilder, loggerFactory:ILoggerFactory) = 
        loggerFactory.AddConsole() |> ignore

        app.Run(fun httpContext -> 
            async {
                let uri = UriHelper.GetEncodedUrl(httpContext.Request)

                let req = HttpWebRequest.CreateHttp uri
                
                req.AllowReadStreamBuffering <- false
                req.AllowWriteStreamBuffering <- false
                req.AutomaticDecompression <- DecompressionMethods.None
                req.Method <- httpContext.Request.Method
                let inputKeys = [ for k in httpContext.Request.Headers.Keys -> k] |> List.filter(fun k -> List.exists ((=)k) ["Host"; "Content-Length";] |> not)
                
                inputKeys 
                |> List.map (fun k -> struct (k, httpContext.Request.Headers.Item k))
                |> List.iter (fun struct (k,v) -> req.Headers.[k] <- v.Item 0)

                use uncompressedInput = 
                    if httpContext.Request.Headers.ContainsKey("Content-Encoding") && httpContext.Request.Headers.["Content-Encoding"].Item 0 = "gzip" then
                        new IO.Compression.GZipStream(httpContext.Request.Body, IO.Compression.CompressionMode.Decompress, false) :> IO.Stream
                    else httpContext.Request.Body
                
                use input = new System.IO.StreamReader(uncompressedInput)
                
                let! reqStream = req.GetRequestStreamAsync() |> Async.AwaitTask
                
                use compressedOuput = 
                    if httpContext.Request.Headers.ContainsKey("Content-Encoding") && httpContext.Request.Headers.["Content-Encoding"].Item 0 = "gzip" then
                        new IO.Compression.GZipStream(reqStream, IO.Compression.CompressionMode.Compress, true) :> IO.Stream
                    else reqStream

                use output = new System.IO.StreamWriter(compressedOuput)

                let replace = 
                    let replace target dest = 
                        let t = MemoryExtensions.AsMemory target
                        let d = MemoryExtensions.AsMemory dest
                        NonContiguousMemory.replace t d

                    //replace "ve examples in docu" "ve fuucking in docu" >> replace "domain" "fuuuck"
                    replace "P@ssw0rd" "**********"
                
                do! Proxy.run 4096 (fun x -> output.WriteAsync(x) |> Async.AwaitTask) (Proxy.Reader replace) (fun m -> input.ReadBlockAsync(m).AsTask() |> Async.AwaitTask |> Async.map (fun r -> m.Slice(0,r) |> Memory.op_Implicit))
                
                output.Dispose()
                
                let! webResponse = req.GetResponseAsync() |> Async.AwaitTask
                
                use webResponseStream = webResponse.GetResponseStream()
                
                let outputKeys = [ for k in webResponse.Headers.Keys -> k ] |> List.filter(fun k -> List.exists ((=)k) ["Host"] |> not)

                outputKeys
                |> List.map (fun k -> struct (k, webResponse.Headers.Get k) )
                |> List.iter (fun struct (k, v) -> httpContext.Response.Headers.Add(k, Microsoft.Extensions.Primitives.StringValues(v) ) )
                use responseStream = httpContext.Response.Body
                httpContext.Response.ContentLength <- Nullable webResponse.ContentLength
                do! webResponseStream.CopyToAsync(responseStream) |> Async.AwaitTask
            } |> Async.StartAsTask :> System.Threading.Tasks.Task
        )

[<EntryPoint>]
let main argv =
    let config = (ConfigurationBuilder()).Build()
    
    let builder = 
        (new WebHostBuilder()).UseConfiguration(config).UseStartup<Forward>()
            .UseKestrel()
            //.UseWebRoot(System.IO.Directory.GetCurrentDirectory())
            //.UseUrls("http://localhost:8080")
            //.UseUrls("http://localhost:8081", "http://192.168.2.241:8081")
            .UseUrls("http://localhost:5000", "http://192.168.2.241:5000")

    let host = builder.Build()
    host.Run()

    0 // return an integer exit code
