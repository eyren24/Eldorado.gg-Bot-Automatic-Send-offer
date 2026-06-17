using EldoradoApp.Models;

namespace EldoradoApp.Services;

public interface IEldoradoClient
{
    Task<IReadOnlyList<Order>> GetOrdersAsync(CancellationToken cancellationToken = default);
}
