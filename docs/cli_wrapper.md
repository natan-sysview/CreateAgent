# CreateAgent CLI

`CreateAgent.Cli` es un wrapper pequeño para probar la biblioteca desde una
terminal. La lógica de agentes permanece en CreateAgent.

## Flujo mínimo

```bash
createagent request init --provider codex --output request.json
createagent request validate request.json
createagent run request.json --output jsonl
```

Solicitud mínima:

```json
{
  "name": "mi-agente",
  "providerName": "codex",
  "prompt": "Cumple este objetivo y devuelve un resumen."
}
```

## Comandos

| Comando | Función |
|---|---|
| `request init` | Crear un `request.json` de ejemplo |
| `request validate` | Validar una solicitud sin ejecutarla |
| `host init` | Crear la configuración local del CLI |
| `provider probe codex` | Comprobar que Codex está disponible |
| `provider probe claude` | Comprobar que Claude Code está disponible |
| `doctor` | Validar host y proveedor |
| `schema print` | Imprimir el JSON Schema de la solicitud |
| `run` | Ejecutar el worker y publicar eventos |
| `inspect` | Leer el `output.json` de una ejecución |

## Solicitud y host

`request.json` contiene únicamente la intención del agente: nombre, proveedor,
modelo, velocidad, razonamiento, sesión, prompt, workspace, herramientas,
timeout y debug.

`host.json` contiene la configuración de esta instalación: almacenamiento,
concurrencia, canales y rutas de los ejecutables Codex y Claude. Estas opciones
no aparecen en `input.json`.

Ejemplo de `host.json`:

```json
{
  "schemaVersion": "1.0",
  "storageRoot": "./.createagent",
  "maxConcurrentRuns": 4,
  "eventChannelCapacity": 1024,
  "processOutputChannelCapacity": 1024,
  "providers": {
    "codex": {
      "executable": "codex"
    },
    "claude": {
      "executable": "claude"
    }
  }
}
```

## Paths relativos

Los valores de `workspace.sources[].path` y `workspace.readOnlyPaths[]` se
resuelven desde el directorio que contiene `request.json`. Los paths del host
se resuelven desde `host.json`.

## Debug

```json
{
  "debug": true
}
```

La auditoría siempre conserva `input.json`, `output.json` y `events.jsonl`.
`debug: true` agrega únicamente `files.json`.

## Salida JSONL

```json
{"type":"event","data":{}}
{"type":"result","data":{}}
```

Los diagnósticos se escriben en stderr para no contaminar el stream de stdout.

## Cancelación y códigos

`Ctrl+C` cancela la ejecución activa y termina el árbol de procesos.

| Código | Significado |
|---:|---|
| `0` | Éxito |
| `1` | Fallo del agente |
| `2` | Uso incorrecto |
| `3` | Proveedor no disponible |
| `4` | Solicitud o configuración inválida |
| `70` | Error interno |
| `124` | Timeout |
| `130` | Cancelación |
