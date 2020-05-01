### necessite mono à jour ###
https://www.mono-project.com/download/stable/#download-lin-debian

### compilation sous linux : ###
```
mcs -out:DomainScan.exe -platform:x64 -pkg:dotnet -r:DnsClient.dll -r:MySql.Data.dll -r:netstandard.dll -r:System.Net.Http *.cs
```
