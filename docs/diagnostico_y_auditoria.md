# Diagnóstico y expediente auditable

Cada ejecución conserva un expediente pequeño y legible fuera del workspace
del agente:

```text
audit/
  input.json
  output.json
  events.jsonl
  files.json       # solamente en modo debug
```

## Archivos indispensables

### `input.json`

La solicitud recibida por CreateAgent. Sirve para saber qué se pidió, con qué
proveedor, modelo, prompt, workspace y opciones. Los secretos declarados se
guardan redactados.

### `output.json`

El resultado completo de la ejecución: estado, mensaje final, errores,
timestamps, rutas y artefactos. También incluye `executionPlan`, que indica el
ejecutable, argumentos y configuración efectiva usada para arrancar el agente.

Por esta razón no existen archivos separados como `run.json` o `error.json`.

### `events.jsonl`

La actividad observable del agente en orden: progreso, mensajes, herramientas,
stdout, stderr y terminación. Cada línea es un evento JSON independiente.

Por esta razón no existen copias separadas como `execution-trace.jsonl`,
`stdout.log` o `stderr.log`.

### `files.json`

Sólo se crea cuando `debug` es `true`. Contiene únicamente los
archivos creados, modificados o eliminados durante la ejecución. Para los
archivos disponibles incluye metadatos y SHA-256.

Los inventarios anterior y posterior se comparan en memoria; no se guardan
como archivos separados.

## Niveles

| Archivo | `standard` | `debug` |
|---|---:|---:|
| `input.json` | Sí | Sí |
| `output.json` | Sí | Sí |
| `events.jsonl` | Sí | Sí |
| `files.json` | No | Sí |

La auditoría básica siempre está activa; no es una opción del objeto de entrada.

## Seguridad y cuarentena

La evidencia se escribe en `audit/`, no dentro de `workspace/`. CreateAgent no
promete capturar cadenas privadas de pensamiento; conserva sólo información
publicada por el proveedor.

Si la ejecución falla, la política del host puede crear un ZIP de cuarentena
con el workspace final y estos mismos archivos de auditoría.
