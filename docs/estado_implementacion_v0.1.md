# Estado de implementación de CreateAgent v0.1

## Estado

- Versión: `0.1.0-alpha.1`.
- Runtime: .NET 10.
- Adaptador productivo inicial: Codex CLI.
- Baseline inspeccionada: Codex CLI `0.146.0`.
- Distribución: paquetes NuGet locales; publicación pública pendiente.

## Implementado

| Área | Estado |
|---|---|
| Contratos C# tipados | Implementado |
| Contrato JSON estricto y pequeño | Implementado |
| Runtime de procesos multiplataforma | Implementado para ejecutables nativos |
| Streaming stdout/stderr | Implementado |
| Eventos normalizados y payload nativo | Implementado |
| Scheduler concurrente | Implementado en proceso |
| Timeout de cola y ejecución | Implementado |
| Cancelación y process tree | Implementado |
| Workspace con múltiples fuentes | Copia de directorio, contenido o archivo |
| Datos externos pesados | Referencias de solo lectura sin copia mediante `workspace.readOnlyPaths` |
| Permisos privados Unix | Implementado |
| Archivos de agente, skills y SDD | Los prepara el consumidor; se copian o referencian en `workspace` |
| Auditoría filesystem | JSON/JSONL |
| Expediente estándar | `input.json`, `output.json` y `events.jsonl` |
| Diagnóstico debug | `files.json` con archivos creados, modificados o eliminados |
| Redacción literal suministrada por host | Implementado |
| Cuarentena ZIP y SHA-256 | Implementado |
| Límite agregado stdout/stderr | Implementado |
| Wrapper `CreateAgent.Cli` | Implementado como .NET tool |
| Plantillas request/host y schema embebido | Implementado |
| Salida humana y JSONL | Implementado |
| Diagnóstico de host y probe de proveedor | Implementado |
| Cancelación por `Ctrl+C` | Implementado |
| Adaptador Claude Code | Pendiente |
| Adaptador Gemini CLI | Pendiente |
| Resolución de perfiles MCP/herramientas | Contrato listo; host resolver pendiente |
| Persistencia SQLite | Extensión futura; v0.1 usa filesystem |
| Recuperación después de caída del host | Pendiente |
| Publicación NuGet | Pendiente de licencia, repositorio y datos editoriales |

## Política de capacidades en Codex v0.1

El adaptador configura internamente ejecución no interactiva, JSONL,
aprobaciones desactivadas y escritura limitada al workspace. Rechaza:

- `fork` no interactivo;
- `performance: medium`, porque Codex no ofrece un modo medio distinto;
- perfiles de herramientas hasta que exista el resolvedor del host;
- niveles de razonamiento no soportados por Codex.

No se degradan silenciosamente modelo, velocidad, esfuerzo, permisos ni
seguridad.

## Validación automatizada

La suite usa un CLI falso compilado como proceso independiente y cubre:

- éxito y salida final;
- concurrencia real de dos workers;
- límite de concurrencia y timeout de cola;
- stdout y stderr simultáneos;
- stream acotado sin bloquear la terminación;
- cancelación y timeout;
- process tree;
- límite de salida;
- ausencia de resultado final;
- proveedor no disponible antes de copiar el workspace;
- copia de directorios completos y de contenido;
- referencias externas de solo lectura sin copia;
- redacción;
- auditoría normal y `debug`;
- plan efectivo y path del directorio de auditoría;
- expediente mínimo sin datos duplicados;
- `files.json` con cambios y SHA-256;
- ZIP y SHA-256;
- contrato JSON estricto;
- contrato de entrada simplificado;
- argumentos no interactivos del adaptador;
- probe seguro de la instalación local mediante `codex --version`.

Las pruebas específicas del wrapper cubren además:

- generación y validación de solicitudes;
- resolución de paths relativos al JSON;
- rechazo de propiedades desconocidas;
- schema embebido;
- configuración y probe del ejecutable;
- streaming JSONL y comando `inspect`;
- formatos inválidos;
- escritura atómica sin sobrescritura accidental;
- rechazo limpio de configuraciones de proveedor incompletas.

La suite actual contiene 32 pruebas: 22 del runtime y 10 del wrapper CLI.

Las pruebas ordinarias nunca invocan una sesión real de IA.
