// Learn more about F# at http://fsharp.org

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open System.Net
open Microsoft.AspNetCore.Http.Extensions
open System

type Startup (env:IHostingEnvironment) = 
    member val public Configuration = (new ConfigurationBuilder()).AddEnvironmentVariables().Build() with get, set

    member public __.Configure(app:IApplicationBuilder, loggerFactory:ILoggerFactory) = 
        loggerFactory.AddConsole() |> ignore

        app.Run(fun httpContext -> 
            async {
                use b = System.Buffers.MemoryPool.Shared.Rent 4096
                
                let forward () = 
                    async {
                        let uri = UriHelper.GetEncodedUrl(httpContext.Request)
                        let req = HttpWebRequest.CreateHttp uri
                        
                        req.AllowReadStreamBuffering <- false
                        req.AllowWriteStreamBuffering <- false

                        let! response = req.GetResponseAsync() |> Async.AwaitTask
                        use streamResponse = new System.IO.StreamReader(response.GetResponseStream())
                        use outStream = new System.IO.StreamWriter(httpContext.Response.Body)
                        let rom = System.Memory.op_Implicit b.Memory
                        
                        let target = System.MemoryExtensions.AsMemory "Domain"
                        let dest   = System.MemoryExtensions.AsMemory "Fuuuck"
                        
                        let replace copy (rom:ReadOnlyMemory<_>) = 
                            let rec replace (rom:ReadOnlyMemory<_>) = 
                                async {
                                    let idx = System.MemoryExtensions.IndexOf(rom.Span, target.Span, System.StringComparison.InvariantCultureIgnoreCase)                                    
                                    if idx = -1 then do! copy rom //Maybe I need to 
                                    else
                                        if idx > 0 then do! copy (rom.Slice(0, idx))
                                        do! copy dest
                                        
                                        do! replace (rom.Slice(idx + dest.Length))
                                }
                            replace rom
                        let copy (rom:ReadOnlyMemory<_>) = outStream.WriteAsync(rom) |> Async.AwaitTask
                        let rec sync () = 
                            async {
                                let! r = streamResponse.ReadBlockAsync(b.Memory).AsTask() |> Async.AwaitTask
                                if r > 0 then
                                    //do! outStream.WriteAsync(rom.Slice(0,r)) |> Async.AwaitTask
                                    do! rom.Slice(0, r) |> replace copy
                                    do! sync () }
                        do! sync ()
                    }

                do! forward ()
            } |> Async.StartAsTask :> System.Threading.Tasks.Task)

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
