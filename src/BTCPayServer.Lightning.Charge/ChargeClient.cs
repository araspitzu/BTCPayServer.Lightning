﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning.CLightning;
using NBitcoin;
using Newtonsoft.Json;

namespace BTCPayServer.Lightning.Charge
{
    public class ChargeClient : ILightningClient
    {
        private Uri _Uri;
        public Uri Uri
        {
            get
            {
                return _Uri;
            }
        }
        private Network _Network;
        static HttpClient _SharedClient = new HttpClient();
        HttpClient _Client;
        public ChargeClient(Uri uri, Network network): this(uri, network, null)
        {

        }
        public ChargeClient(Uri uri, Network network, HttpClient httpClient)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));
            if (network == null)
                throw new ArgumentNullException(nameof(network));
            httpClient = httpClient ?? _SharedClient;
            _Client = httpClient;
            this._Uri = uri;
            this._Network = network;
            if (uri.UserInfo == null)
                throw new ArgumentException(paramName: nameof(uri), message: "User information not present in uri");
            var userInfo = uri.UserInfo.Split(':');
            if (userInfo.Length != 2)
                throw new ArgumentException(paramName: nameof(uri), message: "User information not present in uri");
            ChargeAuthentication = new ChargeAuthentication.UserPasswordAuthentication(new NetworkCredential(userInfo[0], userInfo[1]));
        }
        public ChargeClient(Uri uri, string cookieFilePath, Network network): this(uri, cookieFilePath, network, null)
        {

        }
        public ChargeClient(Uri uri, string cookieFilePath, Network network, HttpClient httpClient)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));
            if (network == null)
                throw new ArgumentNullException(nameof(network));
            if (cookieFilePath == null)
                throw new ArgumentNullException(nameof(cookieFilePath));
            httpClient = httpClient ?? _SharedClient;
            _Client = httpClient;
            this._Uri = uri;
            this._Network = network;
            ChargeAuthentication = new ChargeAuthentication.CookieFileAuthentication(cookieFilePath);
        }

        public async Task<CreateInvoiceResponse> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken cancellation = default(CancellationToken))
        {
            var message = CreateMessage(HttpMethod.Post, "invoice");
            Dictionary<string, string> parameters = new Dictionary<string, string>();
            parameters.Add("msatoshi", request.Amount.MilliSatoshi.ToString(CultureInfo.InvariantCulture));
            parameters.Add("expiry", ((int)request.Expiry.TotalSeconds).ToString(CultureInfo.InvariantCulture));
            if(request.Description != null)
                parameters.Add("description", request.Description);
            message.Content = new FormUrlEncodedContent(parameters);
            var result = await _Client.SendAsync(message, cancellation);
            result.EnsureSuccessStatusCode();
            var content = await result.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<CreateInvoiceResponse>(content);
        }

        public async Task<ChargeSession> Listen(CancellationToken cancellation = default(CancellationToken))
        {
            return new ChargeSession(
                await WebsocketHelper.CreateClientWebSocket(Uri.ToString(), 
                    $"Basic {ChargeAuthentication.GetBase64Creds()}", cancellation));
        }

        public ChargeAuthentication ChargeAuthentication { get; set; }

        public GetInfoResponse GetInfo()
        {
            return GetInfoAsync().GetAwaiter().GetResult();
        }

        public async Task<ChargeInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default(CancellationToken))
        {
            var request = CreateMessage(HttpMethod.Get, $"invoice/{invoiceId}");
            var message = await _Client.SendAsync(request, cancellation);
            if (message.StatusCode == HttpStatusCode.NotFound)
                return null;
            message.EnsureSuccessStatusCode();
            var content = await message.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<ChargeInvoice>(content);
        }

        public async Task<GetInfoResponse> GetInfoAsync(CancellationToken cancellation = default(CancellationToken))
        {
            var request = CreateMessage(HttpMethod.Get, "info");
            var message = await _Client.SendAsync(request, cancellation);
            message.EnsureSuccessStatusCode();
            var content = await message.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<GetInfoResponse>(content);
        }

        private HttpRequestMessage CreateMessage(HttpMethod method, string path)
        {
            var uri = GetFullUri(path);
            var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", ChargeAuthentication.GetBase64Creds());
            return request;
        }

        private Uri GetFullUri(string partialUrl)
        {
            var uri = _Uri.AbsoluteUri;
            if (!uri.EndsWith("/", StringComparison.InvariantCultureIgnoreCase))
                uri += "/";
            return new Uri(uri + partialUrl);
        }

        async Task<LightningInvoice> ILightningClient.GetInvoice(string invoiceId, CancellationToken cancellation)
        {
            var invoice = await GetInvoice(invoiceId, cancellation);
            if (invoice == null)
                return null;
            return ChargeClient.ToLightningInvoice(invoice);
        }

        async Task<ILightningInvoiceListener> ILightningClient.Listen(CancellationToken cancellation)
        {
            return await Listen(cancellation);
        }

        internal static LightningInvoice ToLightningInvoice(ChargeInvoice invoice)
        {
            return new LightningInvoice()
            {
                Id = invoice.Id ?? invoice.Label,
                Amount = invoice.MilliSatoshi,
                AmountReceived = invoice.MilliSatoshiReceived,
                BOLT11 = invoice.PaymentRequest,
                PaidAt = invoice.PaidAt,
                ExpiresAt = invoice.ExpiresAt,
                Status = CLightningClient.ToStatus(invoice.Status)
            };
        }

        async Task<LightningInvoice> ILightningClient.CreateInvoice(LightMoney amount, string description, TimeSpan expiry, CancellationToken cancellation)
        {
            var invoice = await CreateInvoiceAsync(new CreateInvoiceRequest() { Amount = amount, Expiry = expiry, Description = description ?? "" }, cancellation);
            return new LightningInvoice() { Id = invoice.Id, Amount = amount, BOLT11 = invoice.PayReq, Status = LightningInvoiceStatus.Unpaid, ExpiresAt = DateTimeOffset.UtcNow + expiry };
        }

        async Task<LightningNodeInformation> ILightningClient.GetInfo(CancellationToken cancellation)
        {
            var info = await GetInfoAsync(cancellation);
            return CLightning.CLightningClient.ToLightningNodeInformation(info);
        }

        Task<PayResponse> ILightningClient.Pay(string bolt11, CancellationToken cancellation)
        {
            throw new NotSupportedException();
        }

        Task<OpenChannelResponse> ILightningClient.OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation)
        {
            throw new NotSupportedException();
        }

        Task<BitcoinAddress> ILightningClient.GetDepositAddress()
        {
            throw new NotSupportedException();
        }

        Task ILightningClient.ConnectTo(NodeInfo nodeInfo)
        {
            throw new NotSupportedException();
        }

        Task<LightningChannel[]> ILightningClient.ListChannels(CancellationToken cancellation)
        {
            throw new NotSupportedException();
        }
    }
}
