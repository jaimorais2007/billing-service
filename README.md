# Billing Service (Oficina Mecânica - Tech Challenge Fase 4)

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
| Criar pedido de pagamento | `POST /api/Payments` (Value, ServiceOrderId, ServiceIds, Parts[PartId, Quantity]) | 201 (PaymentUrl, Status, TotalAmount) | 400, 502 |
| Receber webhook do pagamento | `POST /api/webhooks/mercadopago` | 200 + `PaymentConfirmed` ou `PaymentExpired` | 404, 401 |

* Serviços e peças excluídos (inativos) não aparecem na busca nem na listagem e respondem 404.
* O código da peça é único entre as peças ativas.
* No pedido de pagamento, o valor precisa ser positivo e todos os serviços e peças informados precisam existir e estar ativos. Caso contrário, a resposta é 400 (`Invalid or inactive part or service.`). Se o Mercado Pago falhar, a resposta é 502.

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
* Itens técnicos: `OUTBOX` / `<occurredAt>#<eventId>`, com os eventos ainda não publicados no SNS.

---

## Eventos publicados

Tópicos `arn:aws:sns:us-east-1:168126498555:oficina-<Evento>`. O envelope é o do grupo: `eventId`, `eventType`, `occurredAt`, `correlationId` e `payload`.

| Evento | Payload |
|---|---|
| `ServiceDeleted` | `serviceId` |
| `PartDeleted` | `partId` |
| `PartStockAdded` | `partId`, `availableQuantity` |
| `PaymentConfirmed` | `serviceOrderId`, `gatewayTransactionId` |
| `PaymentExpired` | `serviceOrderId`, `gatewayTransactionId` |

O evento é gravado na mesma transação da mudança de estado e publicado em segundo plano, sem limite de tentativas.

---

## Configuração

Secret `billing-service-secret` no namespace `oficina`:

| Chave | Uso |
|---|---|
| `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` | Usuário IAM `oficina-billing-service` |
| `DYNAMODB_TABLE` | `oficina-billing` |
| `MERCADOPAGO_ACCESS_TOKEN` | Access token da aplicação no Mercado Pago |
| `MERCADOPAGO_WEBHOOK_SECRET` | Assinatura secreta, em Suas integrações > Webhooks |

Fora de `Development`, o serviço **não sobe** sem a tabela. Sem as chaves do Mercado Pago ele sobe, mas o pedido de pagamento devolve 502 e o webhook devolve 401.

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
