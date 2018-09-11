# httpproxy
Man in the middle forward proxy

https://docs.oracle.com/cd/E23095_01/Search.93/ATGSearchAdmin/html/s1207adjustingtcpsettingsforheavyload01.html

benchmark:
C:\tools\httpd-2.4.34-o102o-x86-vc14\Apache24\bin> .\ab.exe -k -c 100 -t 60 -n 10000000 -X localhost:5000 http://localhost:8080/example.html

C:\tools\httpd-2.4.34-o102o-x86-vc14\Apache24\bin> .\ab.exe -k -c 100 -t 60 -n 10000000 -X localhost:5000 http://localhost:8080/120K.xml