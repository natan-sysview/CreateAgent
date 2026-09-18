# CreateAgent: estándar del administrador de workers de agentes CLI

## Estado del documento

- Tipo: visión, requisitos y arquitectura de referencia.
- Estado: arquitectura aprobada para la primera versión.
- Implementación: `0.1.0-alpha.1` funcional; evolución en curso.
- Lenguaje objetivo: C# sobre .NET 10 LTS.
- Consumidores soportados en v1: aplicaciones C#/.NET.
- Proveedores implementados: Codex CLI, Claude Code y OpenCode.
- Proveedor previsto: Gemini CLI.
- Propósito: definir una pieza reusable para crear, ejecutar, observar y auditar
  workers basados en agentes CLI sin acoplar el núcleo a un proveedor.

Documentos complementarios:

- [Contrato JSON de ejecución](contrato_json_ejecucion_agente.md)
- [Matriz inicial de capacidades](matriz_capacidades_cli.md)
- [Estado de implementación v0.1](estado_implementacion_v0.1.md)
- [CLI de referencia y pruebas](cli_wrapper.md)
- [Diagnóstico y expediente auditable](diagnostico_y_auditoria.md)

## Índice

1. [Visión](#1-visión)
2. [Definición del producto](#2-definición-del-producto)
3. [Alcance](#3-alcance)
4. [Límite sobre “pensamientos”](#4-límite-sobre-pensamientos)
5. [Arquitectura de referencia](#5-arquitectura-de-referencia)
6. [Contratos públicos propuestos](#6-contratos-públicos-propuestos)
7. [Modelo de eventos](#7-modelo-de-eventos)
8. [Estado y ciclo de vida](#8-estado-y-ciclo-de-vida)
9. [Layout de almacenamiento](#9-layout-de-almacenamiento)
10. [Workspaces](#10-workspaces)
11. [Spec-driven development](#11-spec-driven-development)
12. [Concurrencia](#12-concurrencia)
13. [Progreso](#13-progreso)
14. [Fallos y cuarentena](#14-fallos-y-cuarentena)
15. [Seguridad](#15-seguridad)
16. [Confiabilidad](#16-confiabilidad)
17. [Persistencia](#17-persistencia)
18. [Observabilidad](#18-observabilidad)
19. [Extensibilidad](#19-extensibilidad)
20. [Proyectos C# propuestos](#20-proyectos-c-propuestos)
21. [Pruebas requeridas](#21-pruebas-requeridas)
22. [Criterios de aceptación](#22-criterios-de-aceptación-de-la-primera-versión)
23. [Decisiones arquitectónicas confirmadas](#23-decisiones-arquitectónicas-confirmadas)
24. [Defaults operativos](#24-defaults-operativos-de-la-primera-implementación)
25. [Principio rector](#25-principio-rector)

## 1. Visión

El administrador de workers de agentes CLI será un motor reusable en C# capaz
de crear agentes de trabajo aislados en tiempo real.

Cada worker tendrá:

- una identidad estable;
- una solicitud y un prompt;
- un directorio de trabajo privado;
- instrucciones y skills suministradas por el consumidor;
- documentos de especificación suministrados por el consumidor;
- una instancia independiente del agente CLI seleccionado;
- un stream de eventos observable en tiempo real;
- logs de auditoría;
- estado, progreso y resultado estructurados;
- cancelación y límites operativos;
- aislamiento forense del workspace cuando falle.

La pieza administra el ciclo de vida del agente. No redacta automáticamente el
contenido funcional de las skills, instrucciones o especificaciones.

El núcleo será neutral. Las características particulares de Codex CLI, Claude
Code, OpenCode, Gemini CLI o cualquier proveedor futuro vivirán en adaptadores
separados.

## 2. Definición del producto

Nombre público:

```text
CreateAgent
```

Contrato conceptual:

```text
AgentRunRequest
  -> preparación del workspace
  -> selección del proveedor
  -> ejecución del agente CLI
  -> stream de AgentEvent
  -> AgentRunResult
```

Debe poder consumirse desde:

- un CLI construido en .NET;
- una API o backend ASP.NET Core;
- una aplicación de escritorio .NET;
- un proceso batch .NET;
- otro agente hospedado en .NET;
- un orquestador de múltiples agentes construido en .NET.

El producto principal debe ser una biblioteca. Cualquier CLI, API o interfaz
visual será un adaptador delgado.

### 2.1 Lenguaje y alcance de consumo

La primera versión se implementará en C# sobre .NET 10 LTS y se distribuirá
como paquetes NuGet. El único consumo soportado será mediante APIs .NET
tipadas. No será requisito de v1 llamar la biblioteca directamente desde Rust,
Python, Java, Go, JavaScript u otros lenguajes.

El contrato natural para un consumidor .NET será:

```csharp
AgentRunRequest
    -> IAgentWorkerManager.StartAsync(...)
    -> IAsyncEnumerable<AgentEvent>
    -> AgentRunResult
```

Las APIs públicas usarán las primitivas nativas de .NET cuando corresponda:

- `Task` y `ValueTask` para operaciones asíncronas;
- `IAsyncEnumerable<AgentEvent>` para streaming;
- `CancellationToken` para cancelación;
- records, enums y tipos inmutables para solicitudes y resultados;
- NuGet para distribución y versionado.

Preparar el diseño para un futuro multilenguaje es una restricción de
arquitectura, no una funcionalidad comprometida para v1. Los contratos deberán
ser simples, versionados y serializables, pero no se implementarán todavía
hosts remotos, transportes RPC ni SDKs para otros lenguajes.

```text
Implementación actual: C#/.NET 10 LTS
Consumo soportado en v1: C#/.NET mediante NuGet
Multilenguaje: evolución posible, fuera del alcance actual
```

No se hará multi-targeting a frameworks anteriores salvo que aparezca un
consumidor real que lo requiera. El target inicial será `net10.0`.

## 3. Alcance

### 3.1 Responsabilidades incluidas

El administrador deberá:

1. Crear una ejecución con un identificador único.
2. Crear un workspace aislado para esa ejecución.
3. Copiar los archivos y directorios autorizados.
4. Instalar en el workspace las instrucciones, skills y especificaciones
   recibidas.
5. Seleccionar y validar el adaptador solicitado.
6. Preparar la configuración local permitida para el proveedor.
7. Iniciar el agente CLI sin depender de una terminal interactiva.
8. Entregar el prompt de la ejecución.
9. Leer de manera asíncrona la salida estándar y la salida de error.
10. Interpretar el protocolo estructurado expuesto por el CLI cuando esté
   disponible.
11. Normalizar los eventos comunes sin perder el evento nativo del proveedor.
12. Publicar eventos en tiempo real sin esperar a que termine el proceso.
13. Mantener un estado de ejecución consultable.
14. Registrar acciones, tiempos, resultados y errores.
15. Permitir varias ejecuciones simultáneas con límites configurables.
16. Permitir cancelación y aplicar timeouts.
17. Capturar el resultado final del agente.
18. Clasificar la terminación como exitosa, fallida, cancelada, expirada o
    interrumpida.
19. Sellar y comprimir el workspace de una ejecución fallida.
20. Mover el paquete fallido a un área de cuarentena auditable.
21. Limpiar los workspaces exitosos conforme a una política configurable.

### 3.2 Responsabilidades excluidas

El administrador no deberá:

- inventar el contenido de las skills;
- redactar por sí solo las especificaciones del producto;
- decidir qué cambio funcional debe hacer un agente;
- modificar silenciosamente el prompt recibido;
- ocultar errores del proceso del proveedor;
- compartir un workspace escribible entre workers;
- interpretar heurísticamente texto de consola cuando exista un formato
  estructurado oficial;
- reducir todos los proveedores al mínimo común denominador;
- simular una capacidad que el proveedor no ofrezca;
- registrar secretos o credenciales;
- exponer razonamiento privado que el motor no publique explícitamente;
- declarar éxito únicamente porque el proceso terminó con código cero si el
  contrato de salida requerido no fue satisfecho;
- entregar SDKs para Rust, Python, Java, Go, JavaScript u otros lenguajes;
- exponer bindings FFI o una ABI nativa;
- implementar gRPC, Protocol Buffers, OpenAPI, SSE o WebSockets únicamente para
  habilitar consumo multilenguaje;
- operar como daemon o servicio remoto independiente;
- prometer compatibilidad binaria fuera del ecosistema .NET.

Estas exclusiones no impiden construir un backend ASP.NET Core que consuma
directamente la biblioteca. Solamente dejan fuera de v1 la frontera de
comunicación hacia otros lenguajes.

## 4. Límite sobre “pensamientos”

El sistema solo puede registrar información observable y publicada por el
agente CLI:

- eventos del protocolo del CLI;
- mensajes de progreso;
- resúmenes de razonamiento expuestos por el producto;
- invocaciones de herramientas;
- comandos ejecutados;
- cambios de archivos;
- aprobaciones solicitadas;
- warnings y errores;
- salida final.

No debe prometer, inferir ni almacenar cadenas privadas de pensamiento. Para
auditoría se utilizarán trazas de decisiones observables, evidencia, acciones y
resultados.

## 5. Arquitectura de referencia

```text
Consumer
  |
  v
IAgentWorkerManager
  |
  +-- Run registry and scheduler
  +-- Workspace provisioner
  +-- Provider registry
  +-- Provider capability negotiation
  +-- Agent CLI adapter
  +-- Event stream
  +-- Progress projector
  +-- Audit writer
  +-- Failure quarantine
  +-- Retention service
```

### 5.1 Núcleo de dominio

El núcleo no dependerá directamente de:

- consola;
- ASP.NET;
- una base de datos específica;
- una ubicación fija de cualquier agente CLI;
- convenciones de archivos exclusivas de un proveedor;
- el protocolo de eventos exclusivo de un proveedor;
- un sistema operativo específico;
- un formato concreto de logging.

Contendrá:

- solicitudes;
- resultados;
- estados;
- eventos;
- políticas;
- contratos de servicios;
- reglas del ciclo de vida.

### 5.2 Adaptadores de agentes CLI

Toda dependencia de Codex CLI, Claude Code, Gemini CLI o cualquier otro
ejecutable quedará detrás de `IAgentCliAdapter`.

Responsabilidades:

- localizar y validar el ejecutable;
- detectar su versión y capacidades;
- declarar su identidad y versión de protocolo;
- construir argumentos sin usar concatenación insegura;
- configurar el directorio de trabajo;
- usar los archivos ya preparados por el consumidor dentro del workspace;
- describir cómo debe entregarse la entrada;
- producir un plan declarativo para que el runtime inicie el proceso;
- decodificar eventos estructurados;
- convertir eventos nativos en eventos comunes;
- conservar eventos desconocidos para compatibilidad futura;
- interpretar el código de salida y la evidencia de terminación.

La versión del CLI, los argumentos efectivos y su huella deberán formar parte de
la auditoría.

Adaptadores iniciales previstos:

```text
CodexCliAdapter
ClaudeCodeCliAdapter
OpenCodeCliAdapter
GeminiCliAdapter
```

Cada adaptador será un paquete independiente. Agregar o actualizar un proveedor
no deberá exigir cambios en el scheduler, workspaces, auditoría, cuarentena o
contratos comunes.

### 5.3 Registro y selección de proveedores

El núcleo resolverá el adaptador mediante un identificador estable:

```csharp
public readonly record struct AgentProviderId(string Value);
```

Identificadores iniciales sugeridos:

```text
openai.codex
anthropic.claude
anomaly.opencode
google.gemini-cli
```

El registro deberá rechazar duplicados y nunca seleccionará un proveedor por
casualidad basándose únicamente en qué ejecutable aparezca primero en `PATH`.

### 5.4 Negociación de capacidades

Los proveedores no tienen las mismas características. El núcleo consultará
capacidades antes de aceptar la ejecución.

```csharp
public sealed record AgentProviderCapabilities(
    bool SupportsNonInteractiveExecution,
    bool SupportsStructuredEvents,
    bool SupportsSessionResume,
    bool SupportsSessionFork,
    bool SupportsToolEvents,
    bool SupportsApprovalEvents,
    bool SupportsNativeSkills,
    bool SupportsProviderInstructions,
    bool SupportsModelSelection,
    IReadOnlySet<string> SupportedPerformanceModes,
    IReadOnlySet<string> SupportedReasoningEfforts,
    bool SupportsSandboxConfiguration,
    bool SupportsMcp,
    IReadOnlySet<string> AdditionalCapabilities);
```

Si el proveedor no soporta un valor solicitado, el run será rechazado con un
error claro. El adaptador no degradará silenciosamente modelo, velocidad,
razonamiento ni sesión.

### 5.5 Contrato del adaptador

El runtime común, y no el adaptador, será dueño del proceso del sistema
operativo. De esta forma todos los proveedores compartirán las mismas garantías
de cancelación, drenado de streams, timeouts, process tree, límites y auditoría.

El adaptador entregará un plan de ejecución declarativo:

```csharp
public sealed record AgentCliLaunchPlan(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    AgentInputMode InputMode,
    AgentOutputProtocol OutputProtocol);
```

Interfaz conceptual:

```csharp
public interface IAgentCliAdapter
{
    AgentProviderId ProviderId { get; }

    Task<AgentProviderDescriptor> ProbeAsync(
        CancellationToken cancellationToken);

    Task MaterializeAsync(
        AgentMaterializationContext context,
        CancellationToken cancellationToken);

    Task<AgentCliLaunchPlan> CreateLaunchPlanAsync(
        AgentExecutionContext context,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<AgentEventDraft>> DecodeAsync(
        AgentNativeOutput output,
        CancellationToken cancellationToken);

    ValueTask<AgentProviderCompletion> InterpretCompletionAsync(
        AgentNativeCompletion completion,
        CancellationToken cancellationToken);
}
```

Reglas:

- el adaptador no iniciará procesos por su cuenta;
- el adaptador no usará shell para construir comandos;
- el adaptador no accederá a workspaces ajenos;
- el adaptador no ampliará permisos solicitados;
- el adaptador declarará si su protocolo es estructurado o textual;
- el adaptador conservará la evidencia nativa que pueda auditarse;
- una salida textual solo podrá producir eventos con la confianza que permita
  demostrar; no se inventarán eventos de herramientas o progreso;
- la autenticación permanecerá bajo el mecanismo oficial del CLI instalado y
  no se copiará al workspace;
- las diferencias del proveedor permanecerán en el adaptador y en sus
  extensiones tipadas, no en condicionales repartidos por el núcleo.

### 5.6 Provisionador de workspace

Será responsable de:

- validar paths;
- impedir escapes fuera de la raíz autorizada;
- crear el layout;
- copiar fuentes cuando la política exija aislamiento por copia;
- permitir una estrategia alternativa mediante worktree o proveedor;
- copiar los archivos y directorios indicados por el consumidor;
- establecer permisos privados;
- capturar en memoria el estado inicial de los archivos cuando se use `debug`;
- impedir reutilizar un directorio contaminado.

### 5.7 Scheduler

El scheduler limitará la concurrencia global y, opcionalmente, por proyecto,
cliente o grupo.

No se debe crear un thread dedicado permanentemente por worker. El diseño usará
I/O asíncrono y primitivas de concurrencia acotadas.

Debe soportar:

- capacidad máxima;
- cola acotada;
- prioridades opcionales;
- cancelación antes de iniciar;
- rechazo explícito por saturación;
- política de cierre ordenado;
- recuperación de ejecuciones abandonadas.

### 5.8 Event stream

El stream será la salida primaria de observabilidad en tiempo real.

Los consumidores no deberán leer directamente los streams del proceso.

Opciones de consumo previstas:

- `IAsyncEnumerable<AgentEvent>`;
- `ChannelReader<AgentEvent>`;
- callback asíncrono;
- adaptador posterior a SignalR, SSE, WebSocket o mensajería.

El productor deberá aplicar backpressure o una política explícita. Nunca
permitirá crecimiento ilimitado de memoria porque un consumidor sea lento.

### 5.9 Auditoría

La auditoría persistirá eventos en un formato append-only y legible por
máquina, preferentemente JSON Lines.

Cada registro incluirá como mínimo:

- versión del esquema;
- run ID;
- worker ID;
- número de secuencia;
- timestamp UTC;
- tipo de evento;
- fase;
- nivel;
- payload estructurado;
- procedencia;
- indicador de redacción de secretos.

La salida humana será una proyección; no sustituirá al log estructurado.

### 5.10 Frontera entre núcleo y proveedores

| Responsabilidad | Núcleo común | Adaptador |
|---|---:|---:|
| Cola y concurrencia | Sí | No |
| Creación de workspace | Sí | Complementa |
| Inicio y terminación del proceso | Sí | Entrega el plan |
| Timeout y cancelación | Sí | Declara soporte cooperativo |
| Argumentos del CLI | No | Sí |
| Archivos de instrucciones nativos | No | Sí |
| Skills o extensiones nativas | No | Sí |
| Formato del prompt y entrada | No | Sí |
| Decodificación del output | No | Sí |
| Eventos normalizados | Publica | Traduce |
| Evento nativo | Conserva | Produce |
| Sesiones y reanudación | Orquesta | Implementa si existe |
| Aprobaciones | Aplica política | Traduce capacidades |
| Sandbox y permisos del proveedor | Valida política | Configura si existe |
| Logs, métricas y ZIP de fallo | Sí | Aporta metadata |

Esta frontera evita una falsa abstracción: los proveedores comparten ciclo de
vida e infraestructura, pero conservan sus diferencias reales.

## 6. Contratos públicos propuestos

### 6.1 Solicitud

```csharp
public sealed record AgentRunRequest
{
    public required string Name { get; init; }
    public required string ProviderName { get; init; }
    public string? Model { get; init; }
    public AgentPerformanceMode Performance { get; init; }
    public AgentReasoningOptions Reasoning { get; init; } = new();
    public AgentSessionOptions Session { get; init; } = new();
    public required string Prompt { get; init; }
    public AgentWorkspaceRequest Workspace { get; init; } = new();
    public IReadOnlyList<string> Tools { get; init; } = [];
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public bool Debug { get; init; }
}
```

La solicitud contiene intención, no argumentos internos del proveedor ni
políticas de auditoría del host.

### 6.2 Archivos del workspace

```csharp
public sealed record AgentWorkspaceSource
{
    public required string Path { get; init; }
    public AgentWorkspaceCopyMode Copy { get; init; }
}
```

La aplicación prepara previamente `AGENTS.md`, `.agents`, `.claude`, skills,
SDD, datos y código. CreateAgent copia un directorio completo, sólo su contenido
o un archivo. No existe un argumento separado `agentDefinition`.

### 6.3 Handle de ejecución

```csharp
public interface IAgentRunHandle
{
    string RunId { get; }
    AgentRunState State { get; }
    IAsyncEnumerable<AgentEvent> ReadEventsAsync(
        CancellationToken cancellationToken = default);
    Task<AgentRunSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);
    Task CancelAsync(
        string reason,
        CancellationToken cancellationToken = default);
    Task<AgentRunResult> Completion { get; }
}
```

### 6.4 Administrador

```csharp
public interface IAgentWorkerManager
{
    Task<IAgentRunHandle> StartAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentRunSnapshot?> FindAsync(
        string runId,
        CancellationToken cancellationToken = default);
}
```

### 6.5 Resultado

```csharp
public sealed record AgentRunResult(
    string RunId,
    AgentProviderExecutionInfo Provider,
    AgentRunStatus Status,
    int? ExitCode,
    string? FinalOutput,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string WorkspacePath,
    string AuditLogPath,
    string? FailureBundlePath,
    AgentRunMetrics Metrics,
    AgentFailure? Failure);
```

La información del proveedor conservará:

- identificador;
- nombre;
- versión del ejecutable;
- protocolo detectado;
- modelo efectivo cuando sea observable;
- session ID nativo cuando el proveedor lo exponga;
- capacidades utilizadas;
- capacidades rechazadas por no estar disponibles;
- huella del ejecutable y configuración no secreta.

## 7. Modelo de eventos

Todos los eventos tendrán un envelope estable.

```csharp
public sealed record AgentEvent(
    int SchemaVersion,
    string RunId,
    long Sequence,
    DateTimeOffset Timestamp,
    AgentProviderId ProviderId,
    AgentEventKind Kind,
    string Phase,
    AgentEventLevel Level,
    JsonElement Payload,
    AgentNativeEvent? NativeEvent);
```

`Payload` contendrá la representación normalizada. `NativeEvent` conservará el
tipo y payload originales del proveedor después de aplicar límites y redacción
de secretos.

Tipos mínimos:

- `RunAccepted`
- `RunQueued`
- `WorkspaceCreating`
- `WorkspaceCreated`
- `AgentFilesMaterialized`
- `ProviderStarting`
- `ProviderStarted`
- `AgentMessage`
- `ReasoningSummary`
- `ToolStarted`
- `ToolProgress`
- `ToolCompleted`
- `FileChanged`
- `ApprovalRequested`
- `ProgressChanged`
- `Warning`
- `Error`
- `FinalOutput`
- `ProviderExited`
- `FailureBundleCreating`
- `FailureBundleCreated`
- `RunSucceeded`
- `RunFailed`
- `RunCancelled`
- `RunTimedOut`

El decodificador deberá preservar el evento original cuando la versión del CLI
publique un tipo que la biblioteca todavía no reconozca. Un evento nativo
desconocido se publicará como `ProviderEvent` y no se descartará
silenciosamente.

## 8. Estado y ciclo de vida

```text
Created
  -> Queued
  -> Preparing
  -> Running
  -> Succeeded
     Failed
     Cancelled
     TimedOut
```

Durante un fallo:

```text
Running
  -> Failing
  -> Quarantining
  -> Failed
```

Cada transición deberá:

- ser atómica;
- quedar registrada;
- validar el estado anterior;
- tener una secuencia monotónica;
- emitir un evento;
- actualizar el snapshot consultable.

## 9. Layout de almacenamiento

```text
storage/
  runs/
    YYYY-MM-DD/<nombre>-HHmmss/
      workspace/
      audit/
        input.json
        output.json
        events.jsonl
        files.json       # solamente en debug
  quarantine/
    YYYY-MM-DD/<nombre>-HHmmss.zip
```

`workspace/` es el área de trabajo del agente; `audit/` pertenece al host. El
detalle normativo de niveles, archivos, redacción y cambios está en
[Diagnóstico y expediente auditable](diagnostico_y_auditoria.md).

## 10. Workspaces

Cada ejecución tendrá un workspace exclusivo. Ningún worker podrá escribir en
el workspace de otro.

Estrategias previstas:

- directorio vacío;
- copia segura de una plantilla;
- copia de un proyecto;
- Git worktree;
- proveedor personalizado.

El estándar no seleccionará silenciosamente una estrategia. La solicitud o la
configuración del host deberá declararla.

Antes de iniciar:

- validar que el destino está dentro de la raíz administrada;
- rechazar enlaces simbólicos peligrosos;
- capturar en memoria el estado inicial de los archivos cuando se use `debug`;
- registrar archivos copiados;
- aplicar permisos privados;
- comprobar espacio disponible;
- comprobar que el CLI del proveedor existe y es ejecutable;
- comprobar que su versión satisface la baseline aprobada;
- negociar las capacidades requeridas por la solicitud.

## 11. Spec-driven development

El administrador apoyará SDD mediante copia, no mediante autoría.

El consumidor podrá proporcionar:

- visión;
- requisitos;
- restricciones;
- criterios de aceptación;
- plan;
- contratos;
- decisiones arquitectónicas;
- casos de prueba;
- definición de terminado.

La aplicación prepara la estructura correcta para el proveedor y la entrega en
`workspace.sources`. El núcleo no depende de una convención SDD específica.

## 12. Concurrencia

La concurrencia tendrá límites explícitos:

```csharp
public sealed record AgentConcurrencyOptions(
    int MaxRunningWorkers,
    int MaxQueuedWorkers,
    int EventBufferCapacity,
    TimeSpan QueueTimeout,
    TimeSpan ShutdownTimeout);
```

Reglas:

- un proceso por worker;
- un workspace por ejecución;
- una secuencia de eventos por ejecución;
- concurrencia global acotada;
- ningún diccionario mutable expuesto públicamente;
- cancelación cooperativa seguida de terminación forzada solo cuando expire el
  periodo de gracia;
- drenado independiente de stdout y stderr para evitar deadlocks;
- terminación del process tree cuando lo permita la plataforma;
- políticas explícitas para CPU, memoria, tiempo y tamaño de salida.

## 13. Progreso

El porcentaje de progreso no puede inferirse honestamente solo por cantidad de
texto emitido.

El estándar distinguirá:

- estado del ciclo de vida;
- fase actual;
- actividad reciente;
- hitos observados;
- progreso declarado por el agente;
- progreso estimado por una política externa.

Un snapshot podrá contener:

```csharp
public sealed record AgentProgress(
    string Phase,
    string Activity,
    int? Percent,
    long LastEventSequence,
    DateTimeOffset LastActivityAt,
    IReadOnlyList<AgentMilestone> Milestones);
```

Si el proveedor no proporciona un porcentaje verificable, `Percent` deberá ser
`null`.

## 14. Fallos y cuarentena

Ante una terminación fallida, el administrador deberá:

1. detener el proceso y sus hijos;
2. cerrar los streams;
3. vaciar y cerrar el log;
4. registrar la causa;
5. guardar en `files.json` únicamente los archivos creados, modificados o
   eliminados cuando se use `debug`;
6. redactar secretos conocidos;
7. crear un ZIP reproducible del workspace y evidencia permitida;
8. calcular SHA-256 del ZIP;
9. mover el conjunto al área de cuarentena;
10. aplicar la política privada de permisos;
11. publicar el evento `FailureBundleCreated`;
12. devolver la ruta en el resultado.

El ZIP no deberá incluir:

- tokens;
- credenciales;
- archivos de autenticación globales;
- variables de entorno completas;
- contenido fuera del workspace;
- sockets, pipes o archivos especiales;
- cachés globales de los proveedores.

Si el empaquetado también falla, la ejecución seguirá siendo fallida y
registrará un segundo error sin perder el error original.

## 15. Seguridad

Requisitos mínimos:

- roots de almacenamiento configurados y validados;
- paths internos siempre relativos;
- protección contra `..`, symlinks y path traversal;
- permisos privados en workspaces, logs, configuraciones y ZIP;
- allowlist de variables de entorno propagadas;
- argumentos construidos mediante APIs de procesos, no mediante shell;
- redacción configurable de secretos;
- límites de tamaño para eventos y archivos;
- nunca incluir credenciales en `AgentRunRequest`;
- separar la autenticación de cada proveedor de la definición del agente;
- conservar una política explícita de sandbox y aprobaciones;
- no permitir que un prompt amplíe los permisos del host.

La biblioteca administra procesos, pero no sustituye el sandbox ni la política
de seguridad del proveedor o del sistema operativo.

## 16. Confiabilidad

La implementación deberá cubrir:

- fallo al crear el workspace;
- colisión de run ID;
- ejecutable inexistente;
- versión incompatible del CLI;
- proceso que no inicia;
- proceso que cuelga;
- stdout incompleto;
- stderr muy grande;
- evento JSON inválido;
- evento desconocido;
- proceso que termina sin resultado final;
- cancelación concurrente;
- caída del host;
- disco lleno;
- error al escribir logs;
- error al crear el ZIP;
- worker huérfano después de reiniciar el servicio.

La recuperación de host deberá poder detectar runs no terminales y marcarlos
como `Interrupted` o adoptar el proceso solamente si existe una estrategia
segura y demostrable.

## 17. Persistencia

La primera versión puede usar filesystem como registro durable de cada run.

El diseño no deberá impedir agregar posteriormente:

- SQLite;
- PostgreSQL;
- almacenamiento de objetos;
- un event store;
- una cola distribuida.

`events.jsonl`, `files.json` y `audit/output.json` deben tener versiones de
esquema.

## 18. Observabilidad

Métricas mínimas:

- runs aceptados, rechazados y en cola;
- workers activos;
- duración de espera;
- duración de preparación;
- duración del proceso del proveedor;
- duración total;
- eventos emitidos y descartados;
- bytes de stdout y stderr;
- tamaño del workspace;
- tamaño del paquete de fallo;
- cancelaciones;
- timeouts;
- fallos por categoría;
- tiempo desde el último evento.

La actividad observada no debe confundirse con éxito funcional.

## 19. Extensibilidad

Puntos de extensión previstos:

- `IAgentCliAdapter`
- `IAgentProviderRegistry`
- `IAgentProviderCapabilityDetector`
- `IAgentWorkspaceProvider`
- `IAgentEventDecoder`
- `IAgentAuditSink`
- `IAgentProgressProjector`
- `IAgentFailureBundler`
- `IAgentRunRepository`
- `ISecretRedactor`

Los adaptadores no deberán cambiar las reglas del ciclo de vida.

## 20. Proyectos C# propuestos

```text
CreateAgent.Abstractions
CreateAgent.Core
CreateAgent.Runtime.Processes
CreateAgent.Storage.FileSystem
CreateAgent.Providers.Codex
CreateAgent.Providers.ClaudeCode
CreateAgent.Providers.OpenCode
CreateAgent.Providers.GeminiCli
CreateAgent.AspNetCore
CreateAgent.Cli
CreateAgent.Tests
```

El proyecto `Abstractions` deberá ser pequeño y estable. `Core` no dependerá del
CLI de presentación ni de ningún proveedor. Cada paquete `Providers.*` podrá
depender de `Abstractions` y de la infraestructura mínima necesaria, pero el
núcleo nunca dependerá de un adaptador concreto.

## 21. Pruebas requeridas

Antes de declarar una primera versión:

- prueba de un worker exitoso usando un proceso falso;
- prueba de varios workers simultáneos;
- prueba del límite de concurrencia;
- prueba de backpressure;
- prueba de stdout y stderr simultáneos;
- prueba de cancelación;
- prueba de timeout;
- prueba de process tree;
- prueba de evento inválido;
- prueba de evento desconocido;
- prueba de resultado ausente;
- prueba de path traversal;
- prueba de symlink peligroso;
- prueba de redacción de secretos;
- prueba de ZIP y SHA-256;
- prueba de fallo durante el empaquetado;
- prueba de recuperación de un run interrumpido;
- prueba de selección determinista del proveedor;
- prueba de rechazo por capacidad requerida ausente;
- prueba de rechazo de una capacidad no soportada;
- prueba de preservación de un evento nativo desconocido;
- pruebas de contrato ejecutadas contra todos los adaptadores;
- prueba de compatibilidad con una versión real aprobada de Codex CLI;
- prueba de compatibilidad con una versión real aprobada de Claude Code;
- prueba de compatibilidad con una versión real aprobada de OpenCode;
- prueba de compatibilidad con una versión real aprobada de Gemini CLI;
- prueba end-to-end con un workspace desechable.

Las pruebas normales no deberán consumir una sesión real de ningún proveedor.
La integración real será una suite separada, explícita y configurable por
proveedor.

## 22. Criterios de aceptación de la primera versión

La versión inicial se considerará funcional cuando:

1. Pueda aceptar una solicitud completamente estructurada.
2. Cree un workspace privado y verificable.
3. Materialice instrucciones, skills y specs recibidas.
4. Seleccione y ejecute dinámicamente un adaptador aprobado.
5. Entregue eventos en tiempo real.
6. Mantenga logs JSONL ordenados y auditables.
7. Ejecute varios workers respetando límites.
8. Permita cancelación y timeout.
9. Devuelva un resultado estructurado.
10. Genere una cuarentena ZIP verificable ante fallo.
11. No filtre secretos conocidos.
12. Rechace una ejecución cuando el proveedor no tenga una capacidad requerida.
13. Conserve tanto eventos normalizados como evidencia nativa.
14. Pase las pruebas de concurrencia, aislamiento, proveedores y fallos.

## 23. Decisiones arquitectónicas confirmadas

1. El producto principal será una biblioteca reusable en C# sobre .NET 10 LTS,
   distribuida mediante NuGet.
2. El núcleo será neutral respecto del agente CLI.
3. Cada agente CLI tendrá un adaptador independiente.
4. Codex CLI, Claude Code y OpenCode están implementados; Gemini CLI permanece
   previsto.
5. La biblioteca podrá utilizarse desde un backend web y desde otros productos.
6. El runtime común administrará procesos, concurrencia, workspaces, streaming,
   auditoría, cancelación y cuarentena.
7. Cada adaptador administrará comandos, capacidades, instrucciones, protocolo
   y semántica propia de su proveedor.
8. La biblioteca copiará skills y especificaciones ya preparadas, pero no
   redactará ni transformará su contenido.
9. Los eventos comunes no reemplazarán ni descartarán la evidencia nativa del
   proveedor.
10. La biblioteca recibirá una solicitud JSON pequeña y estricta.
11. La solicitud distinguirá modelo, velocidad y esfuerzo; un adaptador no
    supondrá que son la misma característica.
12. Las skills usarán como base portable el estándar abierto Agent Skills.
13. SDD usará un paquete neutral de especificaciones porque no existe un layout
    nativo común a los tres proveedores.
14. El usuario suministrará el contenido de instrucciones, skills y specs; la
    biblioteca sólo lo copiará al workspace.
15. La primera versión solo soportará consumidores C#/.NET mediante APIs
    tipadas y primitivas asíncronas nativas.
16. Los contratos públicos serán simples y serializables para no cerrar una
    futura evolución multilenguaje.
17. gRPC, Protocol Buffers, OpenAPI, SDKs externos, bindings FFI y un host
    remoto quedan fuera del alcance de v1.
18. El nombre público, namespace raíz, solución y prefijo de paquetes será
    `CreateAgent`.
19. La primera implementación vivirá en el directorio independiente
    `CreateAgent/` dentro del workspace actual y después podrá extraerse a su
    propio repositorio.
20. La superficie principal seguirá siendo la biblioteca NuGet. Se incluye
    `CreateAgent.Cli` como host delgado de referencia y herramienta de pruebas;
    API y bindings permanecen como hosts posteriores.
21. Codex CLI fue el primer adaptador real; Claude Code es el segundo adaptador
    implementado.
22. La estrategia inicial de workspace será copia aislada; Git worktree quedará
    como extensión posterior.
23. La persistencia inicial usará filesystem, cambios de archivos en JSON y
    auditoría JSONL.
24. Windows, Linux y macOS serán las plataformas objetivo del diseño.

La presencia local de los tres CLI permitirá caracterizar y probar sus
adaptadores contra ejecutables reales cuando se autorice la fase de
implementación.

## 24. Defaults operativos de la primera implementación

La primera implementación usará defaults conservadores y configurables:

1. Máximo de workers simultáneos: entre uno y cuatro según CPU.
2. Timeout default por ejecución: treinta minutos.
3. Canal de eventos acotado: 1024 elementos.
4. Aprobaciones interactivas: deshabilitadas para ejecución no interactiva.
5. Sandbox default de Codex: `workspace-write`.
6. Runs fallidos: conservar y empaquetar en cuarentena.
7. Runs exitosos: conservar auditoría y resultado; limpiar el workspace solo
   cuando la política del consumidor lo solicite.
8. Baseline real inicial: Codex CLI `0.131.0`.
9. Capacidades mínimas productivas: ejecución no interactiva, salida
   estructurada, cancelación, instrucciones de proveedor y resultado
   verificable.
10. Modelo, esfuerzo, velocidad y extensiones se traducirán dentro del
    adaptador; el núcleo conservará solamente el contrato neutral.

La primera publicación usa licencia Apache-2.0, el repositorio público de
GitHub y versionado preliminar `0.1.0-alpha.1`. La publicación se realiza desde
GitHub Actions mediante la identidad temporal de confianza de NuGet.org, sin
guardar una API key permanente en el repositorio.

## 25. Principio rector

```text
El administrador no piensa por el agente.
Prepara, ejecuta, observa, controla, audita y aísla.

El núcleo administra agentes.
Cada adaptador entiende a su proveedor.
```
