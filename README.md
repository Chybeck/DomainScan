### Necessite mono à jour ###
https://www.mono-project.com/download/stable/#download-lin-debian

### compilation sous linux : ###
```
 mcs -out:DomainScan.exe -platform:x64 -pkg:dotnet -r:DnsClient.dll -r:MySql.Data.dll -r:netstandard.dll -r:System.Net.Http -r:System.Buffers.dll *.cs
```
### execution sous linux ###
mono DomainScan.exe -mp secretpassword

### execution sous windows ###
faire un raccourci vers le .exe avec le paramètre -mp (ou renseigner dans la source avant compilation)
