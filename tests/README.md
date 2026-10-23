# Estrategia de pruebas

Todavia no hay proyectos de pruebas automatizadas en el repositorio. Al crear las suites, deben reflejar estos limites:

- `Suca.Kernel.Tests`: decisiones de compresion, formatos e integridad.
- `Suca.Application.Tests`: orquestacion, cancelacion, locks y escritura atomica.
- `Suca.Integrations.LmStudio.Tests`: cliente, parser JSON y RAG con servidor simulado.
- `Suca.Api.IntegrationTests`: contratos REST y MCP de extremo a extremo.

No use un servidor LM Studio real para pruebas unitarias. Simule sus respuestas para cubrir variantes de JSON, cancelacion, reintentos y errores de red de forma determinista.
