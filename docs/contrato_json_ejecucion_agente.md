# Contrato de entrada de CreateAgent

## Índice

1. [Objetivo](#1-objetivo)
2. [Objeto estándar](#2-objeto-estándar)
3. [Campos](#3-campos)
4. [Resultado estructurado](#4-resultado-estructurado)
5. [Workspace](#5-workspace)
6. [Herramientas](#6-herramientas)
7. [Valores internos](#7-valores-internos)

## 1. Objetivo

El objeto representa únicamente lo que la aplicación padre solicita al
agente. CreateAgent resuelve internamente los argumentos del proveedor, la
seguridad, la auditoría, la cuarentena y los límites del host.

El JSON Schema vigente está en
[agent-run-request.schema.json](schemas/agent-run-request.schema.json).

## 2. Objeto estándar

```json
{
  "name": "nombre-del-agente",
  "providerName": "codex",
  "model": "modelo-opcional",
  "performance": "normal",
  "reasoning": {
    "effort": "medium"
  },
  "session": {
    "mode": "new"
  },
  "prompt": "Objetivo que debe cumplir el agente.",
  "workspace": {
    "sources": []
  },
  "tools": [],
  "timeout": "PT30M",
  "debug": false
}
```

Los únicos campos obligatorios son:

- `name`;
- `providerName`;
- `prompt`.

## 3. Campos

### `name`

Nombre humano de la ejecución. También se usa para crear un directorio legible.

### `providerName`

Adaptador solicitado. Los valores implementados son `codex`, `claude` y
`opencode`; también se aceptan `openai.codex`, `anthropic.claude` y
`anomaly.opencode`.

### `model`

Modelo solicitado. Si se omite, el proveedor selecciona su modelo normal.
OpenCode requiere elegir `deepseek/deepseek-flash` o
`deepseek/deepseek-v4-pro`.

### `performance`

Velocidad solicitada:

- `normal`;
- `medium`;
- `fast`.

Si un proveedor no ofrece el modo solicitado, su adaptador falla con un error
claro. CreateAgent no cambia la velocidad silenciosamente.

En Claude Code solamente está habilitado `normal`. No existe un modo
`medium`. Aunque Claude ofrece `fast` bajo ciertas condiciones, la cuenta de
prueba respondió `extra_usage_disabled` y ejecutó a velocidad `standard`, así
que CreateAgent lo rechaza hasta poder verificarlo realmente.

OpenCode admite solamente `normal`. Para elegir velocidad o capacidad usa de
forma explícita el modelo DeepSeek Flash o Pro; el adaptador nunca los cambia.

### `reasoning.effort`

Nivel de razonamiento solicitado. Los valores disponibles dependen del
proveedor. El valor normal es `medium`. El adaptador de Codex acepta
`minimal`, `low`, `medium`, `high`, `xhigh`, `max` y `ultra`; el modelo
seleccionado y la versión instalada del CLI deben admitir el nivel solicitado.

Las combinaciones ejecutadas con éxito están registradas en
[Valores de Codex comprobados](guia_para_agentes_desarrolladores.md#valores-de-codex-comprobados).
En el menú de Codex, las etiquetas `Mínimo`, `Medio`, `Alto`, `Muy alta` y
`Ultra` corresponden respectivamente a `low`, `medium`, `high`, `xhigh` y
`ultra`.

En OpenCode, Flash acepta `low`, `high` y `max`; Pro acepta `high` y `max`.

Con Claude Code `2.1.22` están comprobados `low`, `medium` y `high`. Si el probe
detecta una versión con el argumento `--effort`, admite además `xhigh` y `max`;
ambos fueron comprobados con Claude Code `2.1.246` y `claude-opus-4-6`.

### `session`

Controla la conversación del CLI:

```json
{"mode": "new"}
```

Para reanudar una sesión:

```json
{
  "mode": "resume",
  "id": "identificador-de-sesion"
}
```

`id` solamente es válido con `resume`. Los modos actuales son `new`, `resume`
y `ephemeral`. `continue-latest` y `fork` se rechazan porque no permiten
resolver de forma inequívoca el workspace antes de ejecutar el proveedor.

Con `resume`, CreateAgent localiza la sesión dentro de `StorageRoot`, reutiliza
exactamente su workspace original y crea una auditoría nueva. No se permite
enviar `workspace.sources` porque volver a copiar archivos podría sobrescribir
el estado anterior. `workspace.readOnlyPaths` sí puede enviarse nuevamente,
porque controla permisos de esa ejecución y no copia información.

La reanudación requiere que el workspace haya sido conservado. Si no existe,
la sesión es desconocida, pertenece a otro proveedor o ya está ejecutándose,
la corrida falla antes de iniciar el proceso del CLI.

Codex, Claude y OpenCode permiten `resume` por ID. `ephemeral` depende de la
capacidad del adaptador y no queda registrado para una reanudación posterior.

### `prompt`

Texto inicial que contiene el objetivo del agente.

### `outputSchema`

JSON Schema opcional que define el objeto JSON que debe devolver el agente.
Si se omite, el resultado final continúa siendo texto.

### `timeout`

Tiempo máximo de ejecución en formato ISO-8601. El valor normal es `PT30M`.

### `debug`

Cuando es `true`, CreateAgent agrega `audit/files.json` con los archivos
creados, modificados o eliminados. La auditoría básica siempre está activa.

### Resultado y `sessionId`

`AgentRunResult.sessionId` contiene el identificador real informado por el
proveedor. La aplicación padre debe conservarlo si después desea usar
`session.mode: resume`. El registro interno que conecta ese ID con el
workspace se guarda automáticamente en `StorageRoot/sessions`.

## 4. Resultado estructurado

Este ejemplo solicita exactamente cinco campos:

```json
{
  "name": "generar-persona",
  "providerName": "codex",
  "prompt": "Genera los datos ficticios de una persona.",
  "outputSchema": {
    "type": "object",
    "properties": {
      "nombre": { "type": "string" },
      "apellido": { "type": "string" },
      "telefono": { "type": "string" },
      "email": { "type": "string" },
      "direccion": { "type": "string" }
    },
    "required": ["nombre", "apellido", "telefono", "email", "direccion"],
    "additionalProperties": false
  }
}
```

El agente devuelve solamente el objeto solicitado:

```json
{
  "nombre": "Laura",
  "apellido": "García",
  "telefono": "+52 55 1234 5678",
  "email": "laura.garcia@example.com",
  "direccion": "Avenida Central 123"
}
```

`type`, `properties`, `required` y `additionalProperties` son reglas del
esquema; no aparecen en el resultado. El consumidor decide los campos de cada
esquema. CreateAgent no inventa campos de negocio.

El adaptador de Codex materializa el esquema en un archivo temporal porque el
CLI requiere una ruta para `--output-schema`. Ese archivo es interno, no se
copia al workspace y se elimina después de ejecutar. El objeto validado queda
disponible en `AgentRunResult.StructuredOutput` y se registra en
`audit/output.json`. En este modo no se duplica como texto en `FinalMessage`.

Claude recibe el mismo esquema directamente mediante `--json-schema`; tampoco
crea un archivo de resultado adicional.

OpenCode `1.18.30` no ofrece una opción nativa de schema verificada, por lo que
su adaptador rechaza `outputSchema`.

`--json` y `outputSchema` resuelven necesidades distintas: el primero produce
eventos JSONL en tiempo real; el segundo controla el objeto final.

## 5. Workspace

`workspace.sources` permite copiar cualquier archivo o directorio necesario:

```json
{
  "workspace": {
    "sources": [
      {
        "path": "/entrada/agente-codex",
        "copy": "directory"
      },
      {
        "path": "/entrada/datos",
        "copy": "contents"
      },
      {
        "path": "/entrada/catalogo.sqlite",
        "copy": "directory"
      }
    ]
  }
}
```

- `directory`: conserva el nombre del directorio dentro del workspace.
- `contents`: copia solamente el contenido del directorio a la raíz.
- Para un archivo, CreateAgent copia el archivo a la raíz del workspace.

El consumidor prepara previamente las convenciones específicas de cada
proveedor, como `AGENTS.md`, `.agents`, `.claude`, skills, SDD, PDFs, imágenes,
bases SQLite o código fuente. CreateAgent no escribe ni interpreta ese
contenido.

Para información pesada que no debe copiarse, se declaran rutas externas de
solo lectura:

```json
{
  "workspace": {
    "readOnlyPaths": [
      "/datos/proyecto-grande",
      "/datos/catalogos"
    ]
  }
}
```

- La información permanece en su ruta original y no ocupa espacio adicional
  en el workspace.
- CreateAgent comunica las rutas al agente y le permite leerlas, pero no
  modificarlas.
- Una ejecución puede combinar `sources` y `readOnlyPaths`.
- En Codex no se usa `--add-dir` para este caso porque ese argumento concede
  escritura. Se aplica un perfil de permisos de lectura (Codex CLI 0.138.0 o
  posterior).
- En Claude se combina `--add-dir` con reglas que deniegan edición y escritura
  desde el sandbox. `--add-dir` por sí solo no representa solo lectura.
- En OpenCode `1.18.30`, `readOnlyPaths` requiere una allowlist explícita.
  CreateAgent autoriza sólo las rutas externas declaradas, mantiene bloqueado
  `bash` y admite `edit` exclusivamente para escribir dentro del workspace.
  Las rutas externas siguen protegidas contra edición. Para archivos
  individuales usa `tools: ["read", "edit"]`; `glob`, `grep` y `list` se
  admiten únicamente cuando todas las rutas son directorios.

## 6. Herramientas

`tools` contiene nombres de herramientas disponibles para el proveedor:

```json
{
  "tools": [
    "Read",
    "Write"
  ]
}
```

En Claude, una lista vacía conserva las herramientas predeterminadas. Una lista
con valores se envía como allowlist exacta mediante `--tools` y
`--allowedTools`. Para MCP, el consumidor copia un `.mcp.json` al workspace y
puede usar nombres como `mcp__servidor__herramienta`. El adaptador usa
configuración MCP estricta para no cargar servidores ajenos al workspace.

Codex v0.1 todavía rechaza listas no vacías hasta que exista su resolvedor de
herramientas.

En OpenCode, una lista vacía conserva las herramientas normales solamente
cuando no existen `readOnlyPaths`. Una lista con valores se convierte en
allowlist. Con rutas externas, CreateAgent administra internamente
`external_directory`, mantiene `question` y `bash` denegados, protege las rutas
externas contra edición y permite `edit` dentro del workspace cuando se
solicita explícitamente.

## 7. Valores internos

Estos valores ya no forman parte de `AgentRunRequest`:

- `schemaVersion`;
- `unsupportedBehavior`;
- `options`;
- `agentDefinition`;
- `execution`;
- `diagnostics`;
- `audit`;
- `metadata`;
- objetos, listas y valores nulos de características no solicitadas.

Las políticas operativas viven en `CreateAgentOptions` o en el adaptador. La
configuración finalmente utilizada se registra en
`output.json.executionPlan`, no en el objeto de entrada.
