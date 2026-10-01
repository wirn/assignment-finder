# Assignment Finder

Personlig konsultuppdragsbevakare med .NET, Brainville, OpenAI och Google SMTP.
Prototypen stöder lokal import, filter och manuella analyser med beständig testbudget.
ASP.NET-värd, PostgreSQL-migration och notifieringsförhandsvisning finns;
sammanhängande bevakning och serverdrift återstår att verifiera.

- [Kod, konfiguration och kommandon](repo/README.md)
- [Implementationsplan och verifieringsstatus](steps.md)
- [Projektinstruktioner](AGENTS.md)
- [Docker, lokal värd och notifieringar](repo/docs/local-host-and-notifications.md)

Kodroten är `repo/`. Kör bygg, tester och Docker Compose därifrån:

```sh
cd repo
dotnet build AssignmentFinder.slnx -c Release
dotnet run --project tests/AssignmentFinder.ParserChecks -c Release
```

Privata CV-filer, exporter, sessioner, budgetjournaler och hemligheter ligger
utanför Git under `repo/data/` eller i privat miljökonfiguration. De överförs
inte via clone/pull och ska inte läggas i containerimagen.
