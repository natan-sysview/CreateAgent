# Matriz inicial de capacidades de agentes CLI

## Estado de la inspección

Fecha de inspección: 2026-07-28.
Actualización de Codex CLI: 2026-08-04.

Versiones encontradas en la máquina:

```text
Codex CLI    0.146.0
Claude Code  2.1.22
Gemini CLI   0.40.0
```

La matriz combina la ayuda local de esas versiones con documentación oficial.
Las capacidades sujetas a modelo, cuenta, plan, feature flag o política deben
confirmarse con `ProbeAsync` antes de cada ejecución.

## 1. Matriz común

| Capacidad | Codex CLI | Claude Code | Gemini CLI |
|---|---|---|---|
| Ejecución no interactiva | `codex exec` | `claude -p` | `gemini -p` |
| Streaming estructurado | `--json` JSONL | `--output-format stream-json` | `--output-format stream-json` |
| Modelo | `--model` | `--model` | `--model` |
| Sesión nueva | Sí | Sí | Sí |
| Reanudar por ID | `exec resume`/`resume` | `--resume` | `--resume` |
| Continuar última | `--last` | `--continue` | `--resume latest` |
| Fork de sesión | `fork` interactivo | `--fork-session` | No demostrado en la ayuda instalada |
| ID elegido por consumidor | No como contrato general | `--session-id UUID` | No demostrado en la ayuda instalada |
| Nivel de razonamiento | Configuración, dependiente del modelo | `--effort`, dependiente de versión/modelo | No hay flag neutral demostrado |
| Velocidad | `service_tier=fast` cuando esté disponible | Fast mode condicionado por versión/modelo/cuenta | Selección Auto/Pro/Flash y routing |
| MCP | `codex mcp` y configuración | `--mcp-config`, `.mcp.json`, `claude mcp` | `gemini mcp`, settings y allowlist |
| Skills | Agent Skills | Agent Skills | Agent Skills |
| Instrucciones | `AGENTS.md` | `CLAUDE.md` | `GEMINI.md` |
| Herramientas allow/deny | approvals, sandbox y config | `--tools`, allow y deny | Policy Engine y approval mode |
| Sandbox | `--sandbox` | settings/permission modes | `--sandbox` |
| Aprobaciones | `--ask-for-approval` | `--permission-mode` | `--approval-mode` |
| Directorios adicionales | `--add-dir` | `--add-dir` | `--include-directories` |
| Schema de salida | `--output-schema` | `--json-schema` | No demostrado en la ayuda instalada |
| Límite de presupuesto | No demostrado en ayuda | `--max-budget-usd` | No demostrado en ayuda |
| Workspace Git aislado | Administrado externamente | Funciones/worktrees según versión | `--worktree` |

## 2. Codex CLI

Baseline local: `0.146.0`.

Capacidades verificadas en ayuda local:

- `codex exec` no interactivo;
- prompt por argumento o stdin;
- JSONL con `--json`;
- modelo con `--model`;
- reanudación con session ID, thread name o `--last`;
- ejecución efímera;
- JSON Schema para resultado;
- archivo con último mensaje;
- sandbox;
- aprobaciones;
- búsqueda web;
- directorios adicionales;
- perfiles y overrides `-c`;
- administración de MCP y plugins.

Configuración oficial relevante:

- `model_reasoning_effort` ajusta el esfuerzo de modelos compatibles;
- el adaptador acepta `minimal`, `low`, `medium`, `high`, `xhigh`, `max` y
  `ultra`;
- `gpt-5.6-sol` con `model_reasoning_effort=ultra` fue comprobado mediante
  CreateAgent;
- `gpt-5.6-sol` con `model_reasoning_effort=low` y `high` también fue
  comprobado mediante CreateAgent;
- `model_reasoning_summary` controla el resumen observable;
- `service_tier = "fast"` solicita el tier rápido cuando el modelo lo anuncia;
- `AGENTS.md` se carga jerárquicamente;
- skills de repositorio viven en `.agents/skills`.

El adaptador debe usar `codex exec --json` para automatización, no parsear la
TUI.

## 3. Claude Code

Baseline local: `2.1.22`.

Capacidades verificadas en ayuda local:

- modo no interactivo con `--print`;
- `text`, `json` y `stream-json`;
- chunks parciales;
- entrada `text` o `stream-json`;
- modelo y fallback model;
- sesión elegida, reanudada, continuada o bifurcada;
- schema JSON de salida;
- límite de presupuesto;
- herramientas incorporadas permitidas y denegadas;
- MCP desde archivo o JSON;
- configuración MCP estricta;
- permission modes;
- settings por archivo o JSON;
- system prompt y append system prompt;
- plugins y custom agents.

La documentación actual también describe `--effort`, pero la ayuda local
`2.1.22` no lo muestra. La documentación advierte que `claude --help` no enumera
todos los flags; el adaptador deberá usar una tabla de compatibilidad por
versión y una prueba segura.

Fast mode requiere una versión y modelo compatibles y puede incrementar el
coste. La documentación actual indica como requisito Claude Code `2.1.36` o
posterior, por lo que la baseline local `2.1.22` no debe anunciarlo como
capacidad disponible.

Efecto lateral detectado: la consulta local intentó escribir un debug log en
`~/.claude/debug`. El adaptador deberá controlar `CLAUDE_CONFIG_DIR`, debug
paths, permisos y directorios de estado dentro de su política de aislamiento.

## 4. Gemini CLI

Baseline local: `0.40.0`.

Capacidades verificadas en ayuda local:

- modo headless con `--prompt`;
- `text`, `json` y `stream-json`;
- evento inicial con metadata y session ID según la documentación;
- modelo;
- reanudar última sesión o una seleccionada;
- listado y borrado de sesiones;
- sandbox;
- approval modes;
- Policy Engine;
- MCP y allowlist de servidores;
- extensiones;
- Agent Skills;
- hooks;
- worktree;
- directorios adicionales;
- ACP.

Gemini no expone en la ayuda instalada un parámetro general equivalente a
`reasoning.effort`. La preferencia de velocidad o inteligencia se expresa
principalmente mediante:

- modelo exacto;
- selección `auto`;
- familia Pro para mayor razonamiento;
- familia Flash o Flash-Lite para velocidad;
- routing automático.

El adaptador no debe cambiar de Pro a Flash para cumplir `fast` salvo que
`allowModelSubstitution` sea verdadero.

## 5. Skills

Los tres proveedores se basan en el estándar abierto Agent Skills:

```text
<skill>/
  SKILL.md
  scripts/
  references/
  assets/
```

Rutas:

```text
Codex       .agents/skills/<skill>/
Claude      .claude/skills/<skill>/
Gemini      .gemini/skills/<skill>/ o .agents/skills/<skill>/
```

Esto permite que `CreateAgent` reciba una sola fuente portable y deje al
adaptador únicamente la ubicación.

Las extensiones propietarias del frontmatter no son necesariamente portables.

## 6. Instrucciones y Markdown

```text
Codex       AGENTS.md
Claude      CLAUDE.md o .claude/CLAUDE.md
Gemini      GEMINI.md
```

Claude y Gemini soportan imports de Markdown mediante su sintaxis documentada.
Codex permite jerarquía y fallbacks configurables, pero el adaptador no debe
suponer una sintaxis de import no documentada.

Un paquete neutral debe mantener los Markdown originales y generar un
entrypoint estructural por proveedor.

## 7. Spec-driven development

No se identificó un estándar SDD común y nativo.

Gemini cuenta con plan mode; Claude y Codex tienen mecanismos de planificación,
pero eso no convierte sus layouts en un contrato portable.

La biblioteca definirá:

```text
specs/index.md
specs/vision.md
specs/requirements.md
specs/architecture.md
specs/tasks.md
specs/acceptance.md
```

El usuario suministra el contenido y el adaptador hace visible el índice al
agente.

## 8. Capacidades adicionales que debe contemplar el contrato

Además de modelo, velocidad, sesión, inteligencia, prompt y herramientas:

- proveedor y versión mínima;
- modelo exacto, alias o auto;
- fallback de modelo;
- permiso para cambiar modelo por velocidad;
- permiso para usar tier premium;
- presupuesto máximo;
- sandbox;
- política de aprobaciones;
- acceso a red;
- directorios adicionales;
- worktree o estrategia de workspace;
- formato de streaming;
- schema del resultado;
- archivos finales requeridos;
- persistencia o ejecución efímera;
- timeout;
- cancelación;
- políticas de retry;
- perfil de autenticación referenciado, nunca secreto;
- allowlist de variables de entorno;
- plugins, extensions y hooks;
- herramientas permitidas y prohibidas;
- política ante capacidades no soportadas;
- retención y cuarentena.

## 9. Fuentes oficiales

- [Codex CLI developer commands](https://developers.openai.com/codex/cli/reference/)
- [Codex configuration reference](https://developers.openai.com/codex/config-reference/)
- [Codex AGENTS.md](https://developers.openai.com/codex/guides/agents-md/)
- [Codex skills](https://developers.openai.com/codex/skills/)
- [Codex MCP](https://developers.openai.com/codex/mcp/)
- [Claude Code CLI reference](https://code.claude.com/docs/en/cli-usage)
- [Claude Code programmatic mode](https://code.claude.com/docs/en/headless)
- [Claude Code instructions](https://code.claude.com/docs/en/memory)
- [Claude Code skills](https://code.claude.com/docs/en/slash-commands)
- [Claude Code MCP](https://code.claude.com/docs/en/mcp)
- [Claude Code fast mode](https://code.claude.com/docs/en/fast-mode)
- [Gemini CLI reference](https://geminicli.com/docs/cli/cli-reference/)
- [Gemini headless mode](https://geminicli.com/docs/cli/headless/)
- [Gemini GEMINI.md](https://geminicli.com/docs/cli/gemini-md/)
- [Gemini Agent Skills](https://geminicli.com/docs/cli/skills/)
- [Gemini session management](https://geminicli.com/docs/cli/session-management/)
- [Gemini policy engine](https://geminicli.com/docs/reference/policy-engine/)
