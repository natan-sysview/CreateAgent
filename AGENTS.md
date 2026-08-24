# Instrucciones para agentes desarrolladores

## Propósito

CreateAgent administra workers impulsados por CLI agenticos. Antes de integrar
o modificar la biblioteca, lee:

1. `README.md`;
2. `docs/guia_para_agentes_desarrolladores.md`;
3. `docs/contrato_json_ejecucion_agente.md`.

Para elegir modelo y razonamiento de Codex, consulta primero la sección
«Valores de Codex comprobados» de la guía. No supongas que un modelo funciona
solamente porque su nombre es conocido por otro cliente de Codex.

## Reglas del contrato

- `CreateAgentOptions` configura el host: almacenamiento, concurrencia y
  límites.
- `AgentRunRequest` describe un agente específico.
- No agregues configuración del host a `AgentRunRequest`.
- No agregues propiedades públicas, abstracciones ni archivos sin una
  necesidad solicitada y comprobable.
- Las diferencias de Codex, Claude o Gemini pertenecen a su adaptador.
- Skills, SDD, documentación y datos son preparados por el consumidor. Se
  copian mediante `workspace.sources` o se referencian sin escritura mediante
  `workspace.readOnlyPaths`.
- CreateAgent no redacta ni transforma el contenido del agente.
- `--json` representa eventos del proveedor; `outputSchema` representa el
  objeto final. No mezcles ambos contratos.

## Integración correcta

- Crea un manager por aplicación o servicio y reutilízalo.
- Libera el manager con `await using` o durante el cierre del host.
- Consume `ReadEventsAsync()` para información en tiempo real.
- Usa `Completion` y `AgentRunResult.State` como resultado definitivo.
- Revisa `Errors` y `Artifacts` cuando el estado no sea exitoso.
- No coloques credenciales en prompts, JSON o workspaces.

## Cambios en la biblioteca

- Implementa el cambio más pequeño que resuelva el caso real.
- Conserva nombres y rutas legibles para personas; evita GUID visibles cuando
  no sean necesarios.
- Mantén la auditoría sin datos duplicados.
- Actualiza pruebas y documentación cuando cambie un contrato público.
- Ejecuta `dotnet test CreateAgent.sln -c Release` antes de entregar.
- No implementes mejoras encontradas durante otra tarea sin autorización.

## Errores y mejoras encontrados

No ocultes un problema ni lo mezcles con otro cambio. Sigue las plantillas y la
clasificación de `docs/guia_para_agentes_desarrolladores.md`.

Si no te pidieron corregirlo, documenta el hallazgo y detente. Un reporte debe
contener evidencia reproducible y nunca secretos.
