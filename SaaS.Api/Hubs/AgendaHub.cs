using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace SaaS.Api.Hubs
{
    [Authorize]
    public class AgendaHub : Hub
    {
        public const string EventoAgendaAtualizada = "AgendaAtualizada";

        public static string GrupoUsuario(int usuarioId) => $"agenda:{usuarioId}";

        public override async Task OnConnectedAsync()
        {
            if (int.TryParse(Context.UserIdentifier, out var usuarioId) && usuarioId > 0)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GrupoUsuario(usuarioId));
            }

            await base.OnConnectedAsync();
        }
    }
}
