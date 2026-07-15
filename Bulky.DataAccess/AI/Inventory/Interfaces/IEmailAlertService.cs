using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Bulky.DataAccess.AI.Inventory.Interfaces
{
    public interface IEmailAlertService
    {
        [Description("Sends an urgent stock alert email to the administrator. " +
                    "Call this when there are urgent inventory discrepancies that " +
                    "require immediate attention.")]
        Task SendAlertEmailAsync(
           [Description("The subject line of the alert email")] string subject,
           [Description("The full body of the alert email")] string body,
           CancellationToken ct = default);
    }
}
