# Preparación externa de archivos del agente

## Índice

1. [Responsabilidad](#1-responsabilidad)
2. [Patrón recomendado](#2-patrón-recomendado)
3. [Uso con CreateAgent](#3-uso-con-createagent)
4. [Patrón SDD conservado](#4-patrón-sdd-conservado)

## 1. Responsabilidad

CreateAgent no crea, transforma ni interpreta skills, SDD, constituciones o
archivos Markdown. La aplicación consumidora prepara una estructura compatible
con Codex, Claude, OpenCode o Gemini y después pide a CreateAgent que la copie.

No existe un argumento `agentDefinition` ni un manifiesto obligatorio de la
biblioteca.

## 2. Patrón recomendado

Una aplicación puede preparar algo semejante:

```text
agente-preparado/
  AGENTS.md                 # o el archivo propio del proveedor
  governance/
    constitution.md
  prompts/
    initial.md
  skills/
    execute-spec/
      SKILL.md
    validate-spec/
      SKILL.md
  specs/
    index.md
    feature-001/
      spec.md
      plan.md
      tasks.md
      evidence/
```

La estructura exacta depende del proveedor y pertenece a la aplicación padre.

## 3. Uso con CreateAgent

Para conservar el directorio `agente-preparado` dentro del workspace:

```json
{
  "workspace": {
    "sources": [
      {
        "path": "/ruta/agente-preparado",
        "copy": "directory"
      }
    ]
  }
}
```

Para colocar directamente su contenido en la raíz del workspace:

```json
{
  "workspace": {
    "sources": [
      {
        "path": "/ruta/agente-preparado",
        "copy": "contents"
      }
    ]
  }
}
```

## 4. Patrón SDD conservado

Del caso de estudio S05E02 se conserva como recomendación para la aplicación
consumidora:

- una constitución con reglas estables;
- prompts genéricos reutilizables;
- un índice de especificaciones;
- separación entre especificación, plan, tareas y evidencia;
- skills pequeñas con una responsabilidad clara;
- historial y evidencia verificable;
- contenido neutral que pueda adaptarse antes de llegar a CreateAgent.

Este patrón ayuda a construir buenos agentes, pero no forma parte del contrato
de ejecución de la biblioteca.
