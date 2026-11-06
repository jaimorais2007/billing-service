namespace BillingService.Application.Common;

public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message)
    {
    }
}

public class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

public class PaymentGatewayException : Exception
{
    public PaymentGatewayException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
