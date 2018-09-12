# httpproxy
Man in the middle forward/reverse proxy

https://docs.oracle.com/cd/E23095_01/Search.93/ATGSearchAdmin/html/s1207adjustingtcpsettingsforheavyload01.html

benchmark forward proxy:

= Forward =
C:\tools\httpd-2.4.34-o102o-x86-vc14\Apache24\bin> .\ab.exe -k -c 100 -t 60 -n 10000000 -X localhost:5000 http://localhost:8080/example.html

C:\tools\httpd-2.4.34-o102o-x86-vc14\Apache24\bin> .\ab.exe -k -c 100 -t 60 -n 10000000 -X localhost:5000 http://localhost:8080/120K.xml

= Reverse =
C:\tools\httpd-2.4.34-o102o-x86-vc14\Apache24\bin> .\ab.exe -k -H "Accept-Encoding: gzip" -c 1 -t 60 -n 1 -p C:\tools\StaticSite\netcoreapp2.1\120K.xml -T application/xml -X localhost:5000 http://localhost:8081/120K.xml


261rps
.\ab.exe -k -H "Accept-Encoding: gzip" -H "Content-Encoding: gzip" -c 1000 -t 60 -n 1000000 -p C:\tools\StaticSite\netcoreapp2.1\240K.xml.gz -T application/xml -X localhost:5000 http://localhost:8081/120K.xml

252rps
.\ab.exe -k -H "Accept-Encoding: gzip" -c 1000 -t 60 -n 1000000 -p C:\tools\StaticSite\netcoreapp2.1\240K.xml -T application/xml -X localhost:5000 http://localhost:8081/120K.xml

192rps
.\ab.exe -k -c 1000 -t 60 -n 1000000 -p C:\tools\StaticSite\netcoreapp2.1\240K.xml -T application/xml -X localhost:5000 http://localhost:8081/120K.xml