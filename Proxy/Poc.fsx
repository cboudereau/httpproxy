open System.Net

let compress source =
    use sourceStream = System.IO.File.OpenRead(source)
    let targetPath = sprintf "%s.gz" source
    use target = 
        let t = System.IO.File.Open(targetPath, System.IO.FileMode.Create, System.IO.FileAccess.Write)
        new System.IO.Compression.GZipStream(t, System.IO.Compression.CompressionMode.Compress, false)
    sourceStream.CopyTo(target)

//compress """C:\tools\StaticSite\netcoreapp2.1\240K.xml"""

let gzipRequestResponse = 
    let response = 
        let r = WebRequest.CreateHttp("http://192.168.2.241:8081/api/hello", Proxy = WebProxy("http://192.168.2.241:5000", false))
        r.Headers.["Accept-Encoding"] <- "gzip"

        r.Headers.["Content-Encoding"] <- "gzip"

        r.Method <- "POST"
        use body = new System.IO.Compression.GZipStream(r.GetRequestStream(), System.IO.Compression.CompressionMode.Compress, true)
        use source = System.IO.File.OpenRead("""C:\tools\StaticSite\netcoreapp2.1\240K.xml""")
        source.CopyTo(body)
        body.Dispose()

        use reader = new System.IO.StreamReader(new System.IO.Compression.GZipStream(r.GetResponse().GetResponseStream(), System.IO.Compression.CompressionMode.Decompress, false))
        reader.ReadToEnd()
    response.Contains("**********")

let gzipResponseOnly = 
    let response = 
        let r = WebRequest.CreateHttp("http://192.168.2.241:8081/api/hello", Proxy = WebProxy("http://192.168.2.241:5000", false))
        r.Headers.["Accept-Encoding"] <- "gzip"

        r.Method <- "POST"
        use body = r.GetRequestStream()
        use source = System.IO.File.OpenRead("""C:\tools\StaticSite\netcoreapp2.1\240K.xml""")
        source.CopyTo(body)
        body.Dispose()
        let response = r.GetResponse()
        use reader = new System.IO.StreamReader(new System.IO.Compression.GZipStream(response.GetResponseStream(), System.IO.Compression.CompressionMode.Decompress, false))
        reader.ReadToEnd()
    response.Contains("**********") 
    
let noCompression = 
    let response = 
        let r = WebRequest.CreateHttp("http://192.168.2.241:8081/api/hello", Proxy = WebProxy("http://192.168.2.241:5000", false))
        r.Method <- "POST"
        use body = r.GetRequestStream()
        use source = System.IO.File.OpenRead("""C:\tools\StaticSite\netcoreapp2.1\240K.xml""")
        source.CopyTo(body)
        body.Dispose()
        let response = r.GetResponse()
        use reader = new System.IO.StreamReader(response.GetResponseStream())
        reader.ReadToEnd()
    response.Contains("**********") 
