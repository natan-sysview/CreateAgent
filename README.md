# CreateAgent

CreateAgent es una biblioteca C# para crear y controlar agentes de IA desde
una aplicación, un backend o un CLI.

Actualmente incluye adaptadores para **Codex CLI**, **Claude Code** y
**OpenCode con DeepSeek**. La arquitectura permite agregar otros motores.

## ¿Qué hace?

- Crea un workspace independiente para cada agente.
- Puede reanudar una sesión usando exactamente su workspace original.
- Envía el prompt, modelo, velocidad y nivel de razonamiento.
- Ejecuta varios agentes al mismo tiempo.
- Informa el avance en tiempo real.
- Devuelve texto, JSON estructurado o archivos.
- Registra entrada, salida, fechas y eventos para auditoría.
- Conserva un ZIP del workspace cuando una ejecución falla.
- Puede copiar archivos o consultar directorios externos sin modificarlos.

## Requisitos

- .NET 10 SDK.
- Al menos uno de estos CLI instalado y autenticado: Codex, Claude Code u
  OpenCode.

## Instalación

Instala la primera versión preliminar desde NuGet:

```bash
dotnet add package CreateAgent --version 0.1.0-alpha.1
```

El wrapper de pruebas también puede instalarse como herramienta global:

```bash
dotnet tool install --global CreateAgent.Cli --version 0.1.0-alpha.1
```

## Uso básico desde C#

Crea y ejecuta el agente:

```csharp
using CreateAgent;

await using var manager = CreateAgentRuntime.CreateDefault(
    new CreateAgentOptions
    {
        StorageRoot = "/ruta/datos-createagent",
        MaxConcurrentRuns = 2,
    });

var request = new AgentRunRequest
{
    Name = "hola-mundo",
    ProviderName = "codex",
    Prompt = "Crea un programa Hola Mundo en Python.",
};

var agent = await manager.StartAsync(request);

await foreach (var evento in agent.ReadEventsAsync())
{
    Console.WriteLine(evento.Message);
}

var resultado = await agent.Completion;
Console.WriteLine(resultado.FinalMessage);
Console.WriteLine(resultado.SessionId);
```

`resultado` indica si el agente terminó correctamente, sus fechas de inicio y
fin, qué workspace utilizó y si produjo errores o archivos de auditoría.

Para usar Claude Code cambia `ProviderName` a `"claude"`. También se acepta
el identificador completo `"anthropic.claude"`.

Para OpenCode usa `ProviderName = "opencode"` y elige explícitamente
`deepseek/deepseek-flash` o `deepseek/deepseek-v4-pro`.

## Reanudar una sesión

Conserva el `SessionId` devuelto y envíalo en la siguiente solicitud:

```csharp
var continuacion = await manager.StartAsync(request with
{
    Session = new AgentSessionOptions
    {
        Mode = AgentSessionMode.Resume,
        Id = resultado.SessionId,
    },
    Workspace = new AgentWorkspaceRequest(),
});
```

CreateAgent vuelve al mismo workspace y crea una auditoría nueva. Para poder
reanudar, `RetainSuccessfulWorkspace` debe permanecer en `true`, que es el
valor predeterminado.

## Archivos para el agente

Para copiar archivos al workspace:

```json
{
  "workspace": {
    "sources": [
      {
        "path": "/ruta/documentos",
        "copy": "contents"
      }
    ]
  }
}
```

Para consultar información pesada sin copiarla ni modificarla:

```json
{
  "tools": ["read", "edit"],
  "workspace": {
    "readOnlyPaths": [
      "/ruta/datos-pesados"
    ]
  }
}
```

OpenCode requiere una lista explícita de herramientas cuando usa
`readOnlyPaths`. Con `edit`, CreateAgent permite escribir dentro del workspace,
mantiene las rutas externas en solo lectura y bloquea shell.

## Probar con el CLI incluido

```bash
dotnet run --project src/CreateAgent.Cli -- request init --provider codex --output request.json
dotnet run --project src/CreateAgent.Cli -- run request.json --storage ./runtime-data
```

Sustituye `codex` por `claude` u `opencode` para usar otro motor. Los valores
probados se encuentran en la
[guía para agentes desarrolladores](docs/guia_para_agentes_desarrolladores.md).

## Compilar y probar

```bash
dotnet build CreateAgent.sln -c Release
dotnet test CreateAgent.sln -c Release
```

La documentación completa está en el directorio [docs](docs/).
