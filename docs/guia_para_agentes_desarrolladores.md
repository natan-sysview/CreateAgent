# Guía para agentes desarrolladores

## Índice

1. [Modelo mental](#1-modelo-mental)
2. [Integración recomendada](#2-integración-recomendada)
   - [Valores de Codex comprobados](#valores-de-codex-comprobados)
3. [Patrones y responsabilidades](#3-patrones-y-responsabilidades)
4. [Manejo de errores](#4-manejo-de-errores)
5. [Auditoría y diagnóstico](#5-auditoría-y-diagnóstico)
6. [Reporte de errores](#6-reporte-de-errores)
7. [Propuestas de mejora](#7-propuestas-de-mejora)
8. [Lista de verificación](#8-lista-de-verificación)

## 1. Modelo mental

CreateAgent tiene dos entradas diferentes:

```text
CreateAgentOptions
  -> configuración general de la aplicación
  -> StorageRoot, concurrencia, límites y retención

AgentRunRequest
  -> solicitud de un worker
  -> proveedor, modelo, rendimiento, razonamiento, sesión, prompt,
     workspace, herramientas, timeout y debug
```

No mezcles ambos objetos. Una solicitud de usuario no debe decidir dónde
escribe el servidor ni ampliar sus límites operativos.

El flujo normal es:

```text
crear manager una vez
  -> enviar AgentRunRequest
  -> consumir eventos
  -> esperar Completion
  -> revisar resultado y auditoría
```

## 2. Integración recomendada

```csharp
using CreateAgent;

await using var manager = CreateAgentRuntime.CreateDefault(
    new CreateAgentOptions
    {
        StorageRoot = "/datos/createagent",
        MaxConcurrentRuns = 4,
    });

var request = new AgentRunRequest
{
    Name = "validar-documentos",
    ProviderName = "codex",
    Model = "provider-default",
    Performance = AgentPerformanceMode.Normal,
    Reasoning = new AgentReasoningOptions { Effort = "medium" },
    Session = new AgentSessionOptions { Mode = AgentSessionMode.New },
    Prompt = "Valida los documentos y devuelve un resumen.",
    Workspace = new AgentWorkspaceRequest
    {
        Sources =
        [
            new AgentWorkspaceSource
            {
                Path = "/entradas/documentos",
                Copy = AgentWorkspaceCopyMode.Contents,
            },
        ],
        ReadOnlyPaths = ["/datos/catalogos-pesados"],
    },
    Timeout = TimeSpan.FromMinutes(20),
    Debug = true,
};

var handle = await manager.StartAsync(request);
var eventsTask = ConsumeEventsAsync(handle);
var result = await handle.Completion;
await eventsTask;

if (result.State != AgentRunState.Succeeded)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, result.Errors));
}

static async Task ConsumeEventsAsync(IAgentRunHandle handle)
{
    await foreach (var item in handle.ReadEventsAsync())
    {
        Console.WriteLine($"{item.Sequence} {item.Kind}: {item.Message}");
    }
}
```

En un backend, registra el manager con vida de aplicación. No crees un manager
nuevo para cada petición HTTP.

### Valores de Codex comprobados

La siguiente tabla contiene ejecuciones reales finalizadas con estado
`Succeeded` y código de salida `0`:

| Codex CLI | `providerName` | `model` | `performance` | `reasoning.effort` | Resultado |
|---|---|---|---|---|---|
| `0.131.0` | `codex` | `provider-default` | `normal` | `medium` | Comprobado |
| `0.131.0` | `codex` | `gpt-5.4` | `normal` | `xhigh` | Comprobado |
| `0.131.0` | `codex` | `gpt-5.4-mini` | `normal` | `medium` | Comprobado |
| `0.146.0` | `codex` | `gpt-5.6-sol` | `normal` | `low` | Comprobado |
| `0.146.0` | `codex` | `gpt-5.6-sol` | `normal` | `high` | Comprobado |
| `0.146.0` | `codex` | `gpt-5.6-sol` | `normal` | `ultra` | Comprobado |

Correspondencia del menú de Codex mostrado en la aplicación:

| Etiqueta visible | Valor de `reasoning.effort` | Estado |
|---|---|---|
| Mínimo | `low` | Comprobado |
| Medio | `medium` | Comprobado |
| Alto | `high` | Comprobado |
| Muy alta | `xhigh` | Comprobado |
| Ultra | `ultra` | Comprobado |

El adaptador también reconoce `minimal` y `max`, pero no corresponden a las
cinco opciones mostradas en ese menú y todavía no tienen una ejecución real
registrada en esta matriz. No confundas la etiqueta visible «Mínimo» con
`minimal`: para ese menú el valor comprobado es `low`.

Usa `gpt-5.6-sol` con `ultra` cuando necesites el nivel de razonamiento más
alto comprobado en la versión actual:

```json
{
  "providerName": "codex",
  "model": "gpt-5.6-sol",
  "performance": "normal",
  "reasoning": {
    "effort": "ultra"
  }
}
```

`gpt-5.6-sol` no funcionó con `codex-cli 0.131.0`; quedó comprobado después de
actualizar a `0.146.0`. Vuelve a comprobar la matriz cuando cambie la versión
instalada del CLI.

## 3. Patrones y responsabilidades

### Manager reutilizable

El manager mantiene concurrencia, registro de runs y ciclo de vida. Debe
reutilizarse y cerrarse correctamente.

### Adaptador por proveedor

Cada CLI tiene argumentos y capacidades diferentes. Traduce esas diferencias
en `IAgentCliAdapter`; no repartas condicionales de proveedores por el núcleo.

### Workspace preparado por el consumidor

La aplicación prepara skills, SDD, `AGENTS.md`, `.claude`, PDFs, bases SQLite o
código. Usa `workspace.sources` cuando deban copiarse y
`workspace.readOnlyPaths` cuando deban permanecer fuera del workspace sin
posibilidad de modificación. No uses `--add-dir` para simular solo lectura en
Codex: concede escritura.

### Resultado definitivo

Los eventos sirven para observación. `AgentRunResult` es la autoridad para
decidir éxito, fallo, cancelación o timeout.

### Cambios mínimos

No diseñes características futuras durante una integración. Si una capacidad
no existe, repórtala como mejora; no amplíes el contrato automáticamente.

## 4. Manejo de errores

### Solicitud inválida

`CreateAgentValidationException` indica que el request no cumple el contrato.
Muestra sus errores al desarrollador y corrige la solicitud. No reintentes sin
cambiarla.

### Adaptador no registrado

`AgentProviderUnavailableException` antes de aceptar el run indica que
`providerName` no tiene un adaptador registrado.

### Fallo durante la ejecución

El run termina con un `AgentRunResult` no exitoso. Revisa:

1. `State` y `Termination`;
2. `Errors`;
3. `ExitCode`;
4. `AuditDirectory`;
5. `Artifacts`, que puede incluir el ZIP de cuarentena.

### Cancelación y timeout

Usa `handle.CancelAsync()` para cancelación explícita. Un timeout configurado
en el request termina el árbol de procesos y se refleja en el resultado.

## 5. Auditoría y diagnóstico

Revisa los archivos en este orden:

1. `input.json`: qué solicitó la aplicación;
2. `output.json`: resultado y plan efectivo;
3. `events.jsonl`: actividad observable en orden;
4. `files.json`: cambios del workspace cuando `debug` es `true`;
5. ZIP de cuarentena: workspace completo cuando un fallo lo genera.

CreateAgent no captura cadenas privadas de pensamiento. `events.jsonl`
contiene únicamente información publicada por el proveedor.

Antes de compartir una auditoría, elimina credenciales, datos personales y
contenido confidencial.

## 6. Reporte de errores

Primero determina la capa probable:

- `core`: ciclo de vida, validación, concurrencia o resultado;
- `workspace`: copia o paths;
- `adapter`: argumentos o interpretación del proveedor;
- `provider`: fallo propio del CLI o servicio externo;
- `host`: configuración de la aplicación consumidora.

Si trabajas dentro de este repositorio y no te pidieron corregir el error,
propón un reporte llamado:

```text
docs/agent-reports/YYYY-MM-DD-bug-nombre-legible.md
```

No uses GUID en el nombre. Usa una sola incidencia por reporte.

Plantilla:

```markdown
# Error: título breve

## Resumen
Qué falló en una frase.

## Entorno
- CreateAgent:
- Proveedor y versión del CLI:
- Sistema operativo:

## Reproducción
1. Pasos mínimos.
2. Solicitud mínima sin secretos.

## Esperado
Comportamiento correcto.

## Obtenido
Comportamiento observado.

## Evidencia
- Mensaje de error:
- Archivo de auditoría o ZIP:
- Evento relevante:

## Clasificación probable
Core, workspace, adaptador, proveedor o host.
```

No incluyas todo el log si bastan unas líneas y una ruta local.

## 7. Propuestas de mejora

Una mejora debe resolver un caso real. Si no fue solicitada, documéntala sin
implementarla.

Nombre recomendado:

```text
docs/agent-reports/YYYY-MM-DD-mejora-nombre-legible.md
```

Plantilla:

```markdown
# Mejora: título breve

## Problema actual
Limitación demostrable.

## Caso de uso
Quién la necesita y para qué.

## Propuesta
Cambio mínimo sugerido.

## Ubicación
Núcleo, adaptador o host.

## Compatibilidad
Impacto en contratos y consumidores existentes.

## Validación
Pruebas necesarias para aceptarla.
```

No presentes una preferencia personal como si fuera un defecto.

## 8. Lista de verificación

Antes de entregar una integración o cambio:

- el manager se reutiliza y se libera correctamente;
- `StorageRoot` permanece en `CreateAgentOptions`;
- el request contiene sólo datos del agente;
- los eventos se consumen sin sustituir el resultado final;
- errores y artefactos se revisan;
- no se registran secretos;
- las diferencias del proveedor permanecen en el adaptador;
- no se agregaron argumentos ni archivos innecesarios;
- pruebas y documentación coinciden con el código.
