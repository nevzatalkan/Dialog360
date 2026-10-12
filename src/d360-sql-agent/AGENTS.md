# Dialog360 Hosted Agent

- Keep this service limited to generating the structured response requested by the Dialog360 API.
- Do not add database credentials, SQL execution, or direct access to the bank database here.
- Preserve the Responses protocol and the `FOUNDRY_PROJECT_ENDPOINT` and `AZURE_AI_MODEL_DEPLOYMENT_NAME` runtime settings.
- SQL returned by the model remains untrusted and must continue through the API's `SqlGuard` and `QueryRunner`.

This project was built with the microsoft-foundry skill. Before working on or answering questions about Foundry agents, read the microsoft-foundry skill first.
