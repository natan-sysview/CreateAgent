# Estado de implementación de CreateAgent v0.1

## Estado

- Versión: `0.1.0-alpha.1`.
- Runtime: .NET 10.
- Adaptadores productivos: Codex CLI, Claude Code y OpenCode con DeepSeek.
- Baselines inspeccionadas: Codex CLI `0.146.0`; Claude Code `2.1.22` y
  `2.1.246`; OpenCode `1.18.30`.
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
| Adaptador Claude Code | Implementado y comprobado con Claude Code `2.1.22` y `2.1.246` |
| Adaptador OpenCode | Implementado y comprobado con OpenCode `1.18.30` y DeepSeek V4 Flash/Pro |
| Adaptador Gemini CLI | Pendiente |
| Herramientas Claude | Allowlist mediante `tools`; MCP mediante `.mcp.json` copiado al workspace |
| Resolución de perfiles de herramientas Codex | Pendiente |
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

## Política de capacidades en Claude Code v0.1

El adaptador usa `claude -p` con eventos `stream-json`, permisos no
interactivos y sandbox. Soporta modelo, sesiones, salida estructurada,
herramientas, `.mcp.json`, skills e instrucciones preparadas en el workspace.

El probe inspecciona la ayuda del ejecutable antes de anunciar capacidades:

- `performance: normal`;
- Claude `2.1.22`: `reasoning.effort` `low`, `medium` y `high`, aplicados con
  `CLAUDE_CODE_EFFORT_LEVEL`;
- Claude `2.1.246`: `low`, `medium`, `high`, `xhigh` y `max`, aplicados con
  `--effort`.

Claude no ofrece una velocidad `medium`. Fast mode requiere Claude Code
`2.1.36` o posterior, Opus 4.6 y extra usage. La prueba con `2.1.246` y Opus
4.6 informó `extra_usage_disabled` y velocidad efectiva `standard`, por lo que
CreateAgent no anuncia `fast` todavía. Los valores no disponibles se rechazan
claramente, sin degradación silenciosa.
Las rutas externas de solo lectura se protegen tanto para herramientas de
edición como para comandos ejecutados dentro del sandbox.

## Política de capacidades en OpenCode v0.1

El adaptador usa `opencode run --pure --format json`, prompt por stdin y
configuración de permisos aislada por proceso. Soporta DeepSeek V4 Flash y V4
Pro, eventos estructurados, herramientas y razonamiento visible en debug.

Solo permite `performance: normal` y `session.mode: new`. Rechaza sesiones
reanudadas, salida con schema, rutas externas de solo lectura y modelos o
variantes no comprobados. No degrada ni sustituye el modelo solicitado.

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
- probe seguro de la instalación local mediante `codex --version`;
- argumentos, eventos, sesiones y seguridad del adaptador Claude Code;
- ejecución compartida de Claude a través del wrapper;
- probe seguro mediante `claude --version`;
- argumentos, eventos, permisos, modelos y variantes del adaptador OpenCode;
- ejecución compartida y cuarentena ZIP de OpenCode mediante el wrapper;
- probe seguro mediante `opencode --version`.

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

La suite actual contiene 67 pruebas: 49 de biblioteca/runtime y 18 del wrapper
CLI.

Las pruebas ordinarias nunca invocan una sesión real de IA. Las pruebas
manuales con Claude Code comprobaron `sonnet` y esfuerzos `low`, `medium` y
`high` en `2.1.22`; `claude-opus-4-6` con `xhigh` y `max` en `2.1.246`; la
creación de un archivo; JSON estructurado; y la protección de una referencia
externa de solo lectura.

Las pruebas con OpenCode `1.18.30` comprobaron DeepSeek V4 Flash con `low`,
`high` y `max`; DeepSeek V4 Pro con `high` y `max`; creación de archivos,
captura de razonamiento y salida final. También confirmaron que sus sesiones
están ligadas al directorio del proyecto.
