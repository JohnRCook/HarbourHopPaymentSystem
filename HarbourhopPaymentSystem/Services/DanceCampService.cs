﻿using HarbourhopPaymentSystem.Data.Repositories;
using HarbourhopPaymentSystem.Responses;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using OtpNet;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using HarbourhopPaymentSystem.Models;
using Serilog;

namespace HarbourhopPaymentSystem.Services
{
    public class DanceCampService
    {
        private readonly BookingPaymentRepository _bookingPaymentRepository;
        private readonly ILogger _logger;
        private readonly DanceCampOptions _danceCampOptions;
        private readonly HttpClient _httpClient;

        public DanceCampService(BookingPaymentRepository bookingPaymentRepository, IOptionsSnapshot<DanceCampOptions> danceCampOptions, ILogger logger)
        {
            _bookingPaymentRepository = bookingPaymentRepository;
            _logger = logger;
            _danceCampOptions = danceCampOptions.Value;
            _httpClient = new HttpClient();
        }

        public async Task<double> GetOwedBookingAmount(int bookingId)
        {
            var bookings = await GetBookingsReport();
            var booking = bookings.FirstOrDefault(b => b.BookingID == bookingId);
            if (booking == null)
            {
                throw new BookingNotFoundException();
            }
            if (booking.Paid.CompareTo(booking.TotalCost) == 0)
            {
                throw new PaymentAlreadyExistsException();
            }
            return booking.AmountOwed;
        }

        public async Task UpdateDanceCampBookingPaymentStatus(string transactionId)
        {
            try
            {
                _logger.Information($"transactionID: {transactionId}");
                var booking = _bookingPaymentRepository.GetBookingPayment(transactionId);

                _logger.Information($"booking ID is: {booking.BookingId}");

                byte[] secretKeyByteArray = Base32Encoding.ToBytes(_danceCampOptions.SecretKey);

                var totp = new Totp(secretKeyByteArray, 30, OtpHashMode.Sha1, 6);
                var totpCode = totp.ComputeTotp();

                var formContent = new FormUrlEncodedContent(
                    new[]
                    {
                            new KeyValuePair<string, string>("BookingID", booking.BookingId.ToString()),
                            new KeyValuePair<string, string>("APIToken", _danceCampOptions.ApiToken),
                            new KeyValuePair<string, string>("Token", totpCode),
                            new KeyValuePair<string, string>("Status", "Completed"),
                            new KeyValuePair<string, string>("Amount", booking.Amount.ToString("F02", CultureInfo.InvariantCulture)),
                            new KeyValuePair<string, string>("Currency", "EUR"),
                            new KeyValuePair<string, string>("TxnID", transactionId),
                            new KeyValuePair<string, string>("FeeAmount", "0"),
                            new KeyValuePair<string, string>("Notes", ""),
                            new KeyValuePair<string, string>("EmailNotes", "")
                    });

                var response = await _httpClient.PostAsync(_danceCampOptions.PaymentReceiveDanceCampUrl, formContent);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var stringContent = await response.Content.ReadAsStringAsync();

                    var result = JsonConvert.DeserializeObject<DanceCampResponse>(stringContent);

                    if (result.Status != "success")
                    {
                        //TODO: discuss notification options. 
                        //There is a chance that mollie payment is successful but update to dancecamp system failed
                        //Generate notification email? 
                        _logger.Error($"Request to Dance Camp was not successfull, transaction id {transactionId}", response);
                    }
                }
                else
                {
                    _logger.Error($"Request to Dance Camp was not successfull, transaction id {transactionId}", response);
                }
            }
            catch (Exception e)
            {
                //TODO: discuss notification options. 
                //There is a chance that mollie payment is successful but update to dancecamp system failed
                //Generate notification email?
                _logger.Error("Error occured while sending Payment Receive to Dance Camp", e);
            }
        }

        public async Task<IEnumerable<BookingReportRow>> GetBookingsReport()
        {
            var report = await _httpClient.GetStringAsync(_danceCampOptions.BookingReportUrl);
            var csvHelper = new CsvHelper.CsvReader(new StringReader(report));
            return csvHelper.GetRecords<BookingReportRow>();
        }
    }
}
