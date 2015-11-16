﻿using System;
using IO = System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Oocx.ACME.Protocol;
using Oocx.ACME.Services;
using Oocx.Asn1PKCS.Asn1BaseTypes;
using static Oocx.ACME.Services.Log;

namespace Oocx.ACME.Client
{
    public class AcmeClient
    {        
        private readonly HttpClient client;

        private Directory directory;

        private string nonce;        
        private JWS jws;

        public AcmeClient(HttpClient client)
        {
            Info($"using server {client.BaseAddress}");

            this.client = client;
            Initialize();
        }

        public AcmeClient(string baseAddress): this(new HttpClient() { BaseAddress =  new Uri(baseAddress) })
        {             
        }

        private void Initialize()
        {
            var keyManager = new KeyStore(Environment.CurrentDirectory);
            var rsa = keyManager.GetOrCreateKey("acme-key");
                        
            jws = new JWS(rsa);
        }

        public async Task<Directory> DiscoverAsync()
        {
            Verbose($"Querying directory information from {client.BaseAddress}" );
            return await GetAsync<Directory>(new Uri("directory", UriKind.Relative));            
        }

        private void RememberNonce(HttpResponseMessage response)
        {
            nonce = response.Headers.GetValues("Replay-Nonce").First();
            Verbose($"nonce from server is {nonce}");
        }

        public async Task<RegistrationResponse> RegisterAsync()
        {
            await EnsureDirectory();            

            Info("creating new registration");

            var request = new RegistrationRequest()
            {                
                Resource = "new-reg",
                Contact = new[] { "mailto:mathias@raacke.info" },
                //Agreement = "https://letsencrypt.org/documents/LE-SA-v1.0.1-July-27-2015.pdf"
            };

            try
            {
                var registration = await PostAsync<RegistrationResponse>(directory.NewRegistration, request);
                Info($"using new registration: {registration.Location}");
                return registration;
            }
            catch (AcmeException ex) when ((int) ex.Response.StatusCode == 409)
            {
                var location = ex.Response.Headers.Location.ToString();
                Info($"using existing registration: {location}");
                return new RegistrationResponse()
                {
                    Location = location
                };
            }            
        }

        private async Task EnsureDirectory()
        {
            if (directory == null || nonce == null)
            {
                directory = await DiscoverAsync();
            }
        }

        public async Task<RegistrationResponse> UpdateRegistrationAsync(string registrationUri)
        {
            await EnsureDirectory();

            Info("updating registration: accepting terms of service");

            var registration = new RegistrationRequest()
            {
                Resource = "reg",
                Contact = new[] { "mailto:mathias@raacke.info" },
                Agreement = "https://letsencrypt.org/documents/LE-SA-v1.0.1-July-27-2015.pdf"
            };

            return await PostAsync<RegistrationResponse>(new Uri(registrationUri), registration);
        }

        public async Task<AuthorizationResponse> NewDnsAuthorizationAsync(string dnsName)
        {
            await EnsureDirectory();

            Info($"requesting authorization for dns identifier {dnsName}");            

            var authorization = new AuthorizationRequest()
            {
                Resource = "new-authz",
                Identifier = new Identifier() {  Type = "dns", Value = dnsName}
            };

            return await PostAsync<AuthorizationResponse>(directory.NewAuthorization, authorization);
        }

        public async Task<PendingChallenge> AcceptSimpleHttpChallengeAsync(Challange challenge)
        {
            await EnsureDirectory();

            Info($"accepting challenge {challenge.Type}");

            // acme / SimpleHttp!
            var simpleHttp = new SimpleHttpChallenge()
            {
                Tls = challenge.Tls,
                Token = challenge.Token
            };
            
            var header = new object();            
            var encodedMessage = jws.Encode(simpleHttp, header);
            var json = JsonConvert.SerializeObject(encodedMessage, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore, Formatting = Formatting.Indented });

            var acmeChallengePath = Environment.CurrentDirectory;

            var challengeFile = IO.Path.Combine(acmeChallengePath, challenge.Token);
            
            IO.File.WriteAllText(challengeFile, json);

            return new PendingChallenge()
                {
                    Instructions = $"Copy {challengeFile} to https://your-server/.well-known/acme/{challenge.Token}",
                    Complete = new Action(async () =>
                    {
                        var challangeRequest = new ChallangeRequest()
                        {
                            Tls = true,
                            Type = challenge.Type
                        };

                        challenge = await PostAsync<Challange>(challenge.Uri, challangeRequest);

                        while ("pending".Equals(challenge?.Status, StringComparison.OrdinalIgnoreCase))
                        {
                            challenge = await GetAsync<Challange>(challenge.Uri);
                            await Task.Delay(4000);
                        }
                    })
                };
        }

        public async Task<CertificateResponse> NewCertificateRequestAsync(byte[] csr)
        {
            await EnsureDirectory();

            Info("requesting certificate");

            var request = new CertificateRequest {Csr = csr.Base64UrlEncoded()};
            var response = await PostAsync<CertificateResponse>(directory.NewCertificate, request);            

            Verbose($"location: {response.Location}");
            Verbose($"link: {response.Link}");            

            return response;
        }

        private async Task<TResult> GetAsync<TResult>(Uri uri) where TResult : class
        {
            return await SendAsync<TResult>(HttpMethod.Get, uri, null);
        }

        private async Task<TResult> PostAsync<TResult>(Uri uri, object message) where TResult : class
        {
            return await SendAsync<TResult>(HttpMethod.Post, uri, message);
        }

        private async Task<TResult> SendAsync<TResult>(HttpMethod method, Uri uri, object message) where TResult : class
        {
            Verbose($"{method} {uri} {message?.GetType()}");
            var nonceHeader = new AcmeHeader { Nonce = nonce };
            Verbose($"sending nonce {nonce}");

            HttpContent content = null;
            if (message != null)
            {
                var encodedMessage = jws.Encode(message, nonceHeader);
                var json = JsonConvert.SerializeObject(encodedMessage, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore, Formatting = Formatting.Indented });
                content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            
            var request = new HttpRequestMessage(method, uri)
            {
                Content = content
            };

            var response = await client.SendAsync(request);
            Verbose($"response status: {(int)response.StatusCode} {response.ReasonPhrase}");

            RememberNonce(response);
            

            if (response.Content.Headers.ContentType.MediaType == "application/problem+json")
            {
                var problemJson = await response.Content.ReadAsStringAsync();
                var problem = JsonConvert.DeserializeObject<Problem>(problemJson);
                Verbose($"error response from server: {problem.Type}: {problem.Detail}");
                throw new AcmeException(problem, response);
            }

            if (typeof(TResult) == typeof(CertificateResponse) && response.Content.Headers.ContentType.MediaType == "application/pkix-cert")
            {                
                var certificateBytes = await response.Content.ReadAsByteArrayAsync();
                var certificateResponse = new CertificateResponse() {Certificate = certificateBytes};
                GetHeaderValues(response, certificateResponse);
                return certificateResponse as TResult;
            }

            var responseContent = await response.Content.ReadAsAsync<TResult>();

            GetHeaderValues(response, responseContent);

            return responseContent;
        }

        private static void GetHeaderValues<TResult>(HttpResponseMessage response, TResult responseContent)
        {
            var properties =
                typeof (TResult).GetProperties(BindingFlags.Public | BindingFlags.SetProperty)
                    .Where(p => p.PropertyType == typeof (string))
                    .ToDictionary(p => p.Name, p => p);
            foreach (var header in response.Headers)
            {
                if (properties.ContainsKey(header.Key))
                {
                    properties[header.Key].SetValue(responseContent, header.Value);
                }
            }
        }
    }
}