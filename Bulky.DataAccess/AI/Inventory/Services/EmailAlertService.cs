using Bulky.DataAccess.AI.Inventory.Interfaces;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Bulky.DataAccess.AI.Inventory.Services
{
    public class EmailAlertService: IEmailAlertService
    {
        private readonly ILogger<EmailAlertService> _logger;
        private readonly IConfiguration _configuration;

        public EmailAlertService(ILogger<EmailAlertService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public async Task SendAlertEmailAsync(string subject,
                                            string body,
                                            CancellationToken ct=default) 
        {
            var smtpHost =    _configuration["Email:SmtpHost"];
            var smtpPort =    int.Parse(_configuration["Email:SmtpPort"] ?? "587");
            var fromAddress = _configuration["Email:FromAddress"]!;
            var fromName =    _configuration["Email:FromName"] ?? "Readify Inventory";
            var toAddress =   _configuration["Email:AdminAddress"]!;
            var appPassword = _configuration["Email:AppPassword"]!;

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromAddress));
            message.To.Add(MailboxAddress.Parse(toAddress));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };


            try {

                using var client = new MailKit.Net.Smtp.SmtpClient();
                await client.ConnectAsync(smtpHost, 
                                        smtpPort, 
                                        SecureSocketOptions.StartTls, ct);
                await client.AuthenticateAsync(fromAddress, appPassword, ct);
                await client.SendAsync(message, ct);
                await client.DisconnectAsync(true, ct);

                _logger.LogInformation(
                                    "[Email] Alert sent — Subject: {Subject}", subject);

            }
            catch (Exception ex) {

                // Email failure is non-fatal. Log and continue — the SignalR
                // alert is already in the browser. Email is a bonus channel.
                _logger.LogError(ex,
                    "[Email] Failed to send alert — Subject: {Subject}", subject);
                throw; // rethrow so EmailAgent catch in orchestrator fires

            }

        }
    }
}
