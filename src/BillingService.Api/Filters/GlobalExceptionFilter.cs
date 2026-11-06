using System.Diagnostics;
using BillingService.Application.Common;
using BillingService.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BillingService.Api.Filters;

public class GlobalExceptionFilter(ILogger<GlobalExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var exception = context.Exception;
        var activity = Activity.Current;

        if (activity != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.AddException(exception);
        }

        switch (exception)
        {
            case DomainException domainEx:
                logger.LogWarning("DomainException handled: {Message}", domainEx.Message);
                context.Result = new BadRequestObjectResult(new { error = domainEx.Message });
                break;

            case NotFoundException notFoundEx:
                logger.LogInformation("NotFoundException handled: {Message}", notFoundEx.Message);
                context.Result = new NotFoundObjectResult(new { error = notFoundEx.Message });
                break;

            case ConcurrencyConflictException conflictEx:
                logger.LogInformation("Conflict handled: {Message}", conflictEx.Message);
                context.Result = new ConflictObjectResult(new { error = conflictEx.Message });
                break;

            case PaymentGatewayException gatewayEx:
                logger.LogError(gatewayEx, "Payment gateway failure: {Message}", gatewayEx.Message);
                context.Result = new ObjectResult(new { error = "Failed to communicate with the payment gateway." })
                {
                    StatusCode = StatusCodes.Status502BadGateway
                };
                break;

            default:
                logger.LogError(exception, "Unhandled exception occurred: {Message}", exception.Message);
                context.Result = new ObjectResult(new { error = "An unexpected internal error occurred." })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
                break;
        }

        context.ExceptionHandled = true;
    }
}
