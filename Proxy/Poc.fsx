open System.Net

let r = 
    let r = WebRequest.CreateHttp("http://www.example.com", Proxy = WebProxy("http://localhost:5000", false))
    use reader = new System.IO.StreamReader(r.GetResponse().GetResponseStream())
    reader.ReadToEnd()

r.Length = 1270