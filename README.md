# Billing Service (Oficina Mecânica - Tech Challenge Fase 4)

[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=jaimorais2007_billing-service&metric=alert_status)](https://sonarcloud.io/summary/overall?id=jaimorais2007_billing-service) [![Cobertura](https://sonarcloud.io/api/project_badges/measure?project=jaimorais2007_billing-service&metric=coverage)](https://sonarcloud.io/component_measures?id=jaimorais2007_billing-service&metric=coverage) [![CI/CD](https://github.com/jaimorais2007/billing-service/actions/workflows/ci-cd.yml/badge.svg?branch=main)](https://github.com/jaimorais2007/billing-service/actions/workflows/ci-cd.yml)

Microsserviço de **serviços, peças e pagamento** da arquitetura v2. Cobre exatamente os casos de uso abaixo, com pagamento pelo **Mercado Pago**.

Feito em **.NET 8**, com Clean Architecture, **DynamoDB** e eventos publicados no **SNS** por meio de outbox. Nenhum serviço acessa o banco de outro.

---

## Casos de uso

| Caso de uso | Rota | Sucesso | Erros |
|---|---|---|---|
| Criar serviço | `POST /api/Service` (Name, Description, DefaultPrice) | 201 (Id, Name, Description, DefaultPrice) | 400 |
| Editar serviço | `PUT /api/Service/{id}` (Name, Description, DefaultPrice) | 200 (Id, Name, Description, DefaultPrice) | 404, 400 |
| Buscar serviço por ID | `GET /api/Service/{id}` | 200 (Id, Name, Description, DefaultPrice) | 404 |
| Listar serviços | `GET /api/Service` | 200 [Id, Name, Description, DefaultPrice] | |
| Deletar serviço | `DELETE /api/Service/{id}` | 200, exclusão lógica + evento `ServiceDeleted` | 404 |
| Criar peça | `POST /api/Parts` (Name, Code, InitialQuantity, Price) | 201 (Id, Name, Code, QuantityInStock, Price) | 400 |
| Editar peça | `PUT /api/Parts/{id}` (Name, Code, Price) | 200 (Id, Name, Code, QuantityInStock, Price) | 404, 400 |
| Buscar peça por ID | `GET /api/Parts/{id}` | 200 (Id, Name, Code, QuantityInStock, Price) | 404 |
| Listar peças | `GET /api/Parts` | 200 [Id, Name, Code, QuantityInStock, Price] | |
| Deletar peça | `DELETE /api/Parts/{id}` | 204, exclusão lógica + evento `PartDeleted` | 404 |
| Adicionar estoque | `POST /api/Parts/{id}/add-stock` (Quantity) | 204 + evento `PartStockAdded` (PartId, AvailableQuantity) | 404, 400 |
| Retirar estoque | `POST /api/Parts/{id}/remove-stock` (Quantity) | 204 | 404, 400 |
| Criar pedido de pagamento | `POST /api/Payments` (ServiceOrderId, Value ou TotalAmount, ServiceIds, Parts[PartId, Quantity]) | 201 (PaymentUrl, Status, TotalAmount, TransactionId); 200 se já houver checkout aberto com o mesmo valor | 400, 409, 502 |
| Baixar estoque da OS aprovada | Evento `ServiceOrderApproved` (ServiceOrderId, Parts[PartId, Quantity]) | `PartStockEnsured` (ServiceOrderId, PartId, Quantity) por peça baixada | |
| Receber webhook do pagamento | `POST /api/webhooks/mercadopago` | 200 + `PaymentConfirmed` ou `PaymentExpired` | 404, 401 |

* Serviços e peças excluídos (inativos) não aparecem na busca nem na listagem e respondem 404.
* O código da peça é único entre as peças ativas.
* No pedido de pagamento, o valor precisa ser positivo e todos os serviços e peças informados precisam existir e estar ativos. Caso contrário, a resposta é 400 (`Invalid or inactive part or service.`). Se o Mercado Pago falhar, a resposta é 502.

### Integração com o OS Service

Segue o desenho da arquitetura v2 (Excalidraw do grupo):

* **Consultas síncronas:** o OS Service chama `GET /api/Service`, `GET /api/Parts`, `GET /api/Service/{id}` e `GET /api/Parts/{id}` para validar itens e obter o preço vigente.
* **Aceitar orçamento:** o OS Service chama `POST /api/Payments` com `ServiceOrderId` e `TotalAmount` e recebe `201 (PaymentUrl, TransactionId)`. O mesmo endpoint atende o caso de uso pelo API Gateway com `Value`, `ServiceIds` e `Parts`. `TransactionId` é o `GatewayTransactionId` (id da preferência no Mercado Pago), o mesmo enviado depois no `PaymentConfirmed`.
* **Pagamento confirmado:** o OS Service consome `PaymentConfirmed`, coloca a OS em execução e publica `ServiceOrderApproved` (ServiceOrderId, Parts[PartId, Quantity]).

### Baixa de estoque da OS aprovada

1. O billing consome `ServiceOrderApproved` na fila `oficina-billing-events`.
2. Para cada peça com saldo suficiente, baixa `QuantityInStock` e publica `PartStockEnsured` (ServiceOrderId, PartId, Quantity). O OS Service usa esse evento para marcar `StockQuantityWasEnsured = true` no `ServiceOrderPart`.
3. Peças sem saldo (ou inexistentes) ficam pendentes: nada é baixado nem publicado para elas. A peça inativa ainda tem estoque físico, então é baixada normalmente.
4. A baixa, os eventos e o registro da mensagem processada (`EVENT#<eventId>`) são gravados na mesma transação. Mensagens repetidas são ignoradas, e conflitos de versão fazem a mensagem voltar para a fila.
5. Quando o estoque de uma peça aumenta, o billing publica `PartStockAdded` (PartId, AvailableQuantity). Para tentar de novo as peças pendentes, o OS Service publica um novo `ServiceOrderApproved` contendo **somente** as peças ainda não garantidas.

### Um pagamento por OS e expiração do checkout

* **Trava:** cada OS tem um item `ORDER#<osId>` com o pagamento ativo, gravado na mesma transação do pagamento com condição sobre o valor anterior. Duas requisições simultâneas para a mesma OS não criam duas cobranças: a segunda recebe 409.
* **Checkout aberto:** se a OS já tiver um checkout `Pending` ou `Refused` dentro do prazo, ele é devolvido (200) em vez de criar outro. Com valor diferente, a resposta é 409. Se a OS já estiver paga, a resposta é 400.
* **Expiração:** o checkout é criado no Mercado Pago com validade de `CheckoutExpirationMinutes` (padrão 60). A cada 5 minutos, os pagamentos `Pending` vencidos são consultados no Mercado Pago: se houver um pagamento aprovado, viram `Paid`; senão, viram `Cancelled` e é publicado `PaymentExpired` (ServiceOrderId, GatewayTransactionId).
* **Aprovação tardia:** se um checkout cancelado for aprovado depois, ele vira `Paid`, exceto quando outro pagamento da mesma OS já está pago. Nesse caso, o erro vai para o log para estorno manual.

### Webhook do Mercado Pago

1. Valida a origem pelo header `x-signature` (HMAC-SHA256 com a assinatura secreta). Sem assinatura válida: 401.
2. Notificações que não são de pagamento são ignoradas (200).
3. Consulta o pagamento no Mercado Pago pelo `data.id` e localiza a `PaymentTransaction` pelo `external_reference`. Se não encontrar: 404.
4. `approved`: status `Paid` e evento `PaymentConfirmed` (ServiceOrderId, GatewayTransactionId). O valor e a moeda precisam bater com a transação.
5. `rejected` ou `cancelled`: status `Refused` e evento `PaymentExpired` (ServiceOrderId, GatewayTransactionId).
6. Notificações repetidas não publicam o evento de novo.

---

## Banco de dados

Tabela única `oficina-billing` no DynamoDB (criada em `infra-terraform/dynamodb.tf`), com uma partição por tabela:

| Tabela | pk / sk | Colunas |
|---|---|---|
| **Service** | `SERVICE` / `<Id>` | `id`, `name`, `description`, `defaultPrice`, `inactive`, `createdAt` |
| **Part** | `PART` / `<Id>` | `id`, `name`, `code`, `quantityInStock`, `price`, `inactive`, `createdAt` |
| **PaymentTransaction** | `PAYMENT` / `<Id>` | `id`, `serviceOrderId`, `totalAmount`, `gatewayTransactionId`, `paymentUrl`, `status`, `createdAt` |

* `status`: `Pending`, `Paid`, `Refused`, `Cancelled` ou `Refunded`.
* `gatewayTransactionId` guarda o id da preferência criada no Mercado Pago e alimenta o GSI `by-gateway-transaction`.
* Cada item tem `version` para concorrência otimista. Um conflito devolve 409.
* Itens técnicos: `OUTBOX` / `<occurredAt>#<eventId>` (eventos ainda não publicados no SNS), `ORDER#<osId>` / `ACTIVE_PAYMENT` (trava de pagamento por OS) e `EVENT#<eventId>` / `PROCESSED` (mensagens já processadas, com `ttl` de 30 dias).

---

## Eventos publicados

Tópicos `arn:aws:sns:us-east-1:168126498555:oficina-<Evento>`. O envelope é o do grupo: `eventId`, `eventType`, `occurredAt`, `correlationId` e `payload`.

| Evento | Payload |
|---|---|
| `PartStockEnsured` | `serviceOrderId`, `partId`, `quantity` |
| `ServiceDeleted` | `serviceId` |
| `PartDeleted` | `partId` |
| `PartStockAdded` | `partId`, `availableQuantity` |
| `PaymentConfirmed` | `serviceOrderId`, `gatewayTransactionId` |
| `PaymentExpired` | `serviceOrderId`, `gatewayTransactionId` |

O evento é gravado na mesma transação da mudança de estado e publicado em segundo plano, sem limite de tentativas.

Evento consumido (fila `oficina-billing-events`): `ServiceOrderApproved`, publicado pelo OS Service.

---

## Configuração

Secret `billing-service-secret` no namespace `oficina`:

| Chave | Uso |
|---|---|
| `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` | Usuário IAM `oficina-billing-service` |
| `DYNAMODB_TABLE` | `oficina-billing` |
| `SQS_QUEUE_URL` | URL da fila `oficina-billing-events` |
| `MERCADOPAGO_ACCESS_TOKEN` | Access token da aplicação no Mercado Pago |
| `MERCADOPAGO_WEBHOOK_SECRET` | Assinatura secreta, em Suas integrações > Webhooks |

Fora de `Development`, o serviço **não sobe** sem a tabela e a fila. Sem as chaves do Mercado Pago ele sobe, mas o pedido de pagamento devolve 502 e o webhook devolve 401.

No painel do Mercado Pago, configure a URL `https://kwzh4dl581.execute-api.us-east-1.amazonaws.com/api/webhooks/mercadopago` com o evento **Pagamentos**.

---

## Executar

```bash
dotnet run --project src/BillingService.Api
```

Sem AWS configurada, em `Development`, os dados ficam em memória e os eventos só aparecem no log. O Swagger fica em `http://localhost:5222/swagger`.

```bash
docker build -t billing-service .
kubectl apply -n oficina -f k8s/
```

---

## Testes

```bash
dotnet test --configuration Release --collect:"XPlat Code Coverage;Format=opencover"
```

Para validar as transações contra um DynamoDB de verdade, suba o DynamoDB Local e defina a variável abaixo. Sem ela, esse teste é ignorado.

```bash
docker run -d -p 8000:8000 amazon/dynamodb-local
DYNAMODB_LOCAL_URL=http://localhost:8000 dotnet test
```


## Observabilidade (OpenTelemetry + New Relic)

Traces, metricas e logs sao exportados via OTLP para o New Relic (`https://otlp.nr-data.net`).
Configuracao em `Configuration/OpenTelemetryConfiguration.cs`; variaveis `OTEL_*` definidas em `k8s/deployment.yaml`.
A license key vem do secret `billing-service-secret`, chave `OTEL_EXPORTER_OTLP_HEADERS` (formato `api-key=<LICENSE_KEY>`).
O exporter so e ativado quando `OTEL_EXPORTER_OTLP_ENDPOINT` esta definido.

## Qualidade e cobertura de testes

A cada PR e a cada merge na `main`, a pipeline ([`ci-templates`](https://github.com/jaimorais2007/ci-templates)) roda os testes com cobertura e:

- **falha se a cobertura de linhas ficar abaixo de 80%** (passo "Checar cobertura mínima");
- envia a análise ao **SonarCloud** e **aguarda o quality gate** (`sonar.qualitygate.wait=true`): se o gate reprovar, a pipeline falha;
- publica o relatório detalhado no resumo da execução e como artefato `coverage-report-billing-service` ([execuções da pipeline](https://github.com/jaimorais2007/billing-service/actions/workflows/ci-cd.yml)).

A `main` é protegida: só aceita PR com a verificação `pipeline / Build, testes e qualidade` aprovada.

**Evidência:** cobertura de **97,3%** no SonarCloud em 07/11/2026, com quality gate aprovado. Os selos acima mostram o valor atual. Painel: [SonarCloud — billing-service](https://sonarcloud.io/summary/overall?id=jaimorais2007_billing-service).
