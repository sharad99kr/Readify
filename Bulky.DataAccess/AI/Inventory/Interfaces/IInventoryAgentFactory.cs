using Microsoft.Agents.AI;
namespace Bulky.DataAccess.AI.Inventory.Interfaces
{
    public interface IInventoryAgentFactory
    {
        AIAgent CreateSqlAgent(); //has the get_low_stock_products tool
        AIAgent CreateNotificationAgent(); //phrases the admin briefing
        AIAgent CreateExcelAgent(); //reads the warehouse stock from Excel
        AIAgent CreateReconciliationAgent(); //compares SQL vs Excel and generates a report
        AIAgent CreateEmailAgent(); //sends the email to the admin with the report
    }
}
