open System.Net

let r = 
    let r = WebRequest.CreateHttp("http://example.com/", Proxy = WebProxy("http://localhost:5000"))
    use reader = new System.IO.StreamReader(r.GetResponse().GetResponseStream())
    reader.ReadToEnd()

r.Length = 1270