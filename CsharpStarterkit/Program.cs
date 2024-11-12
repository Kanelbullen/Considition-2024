using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net.Http.Headers;

namespace QLearningGame
{
    class Program
    {
        private static readonly string gameUrl = "http://localhost:8080/";
        private static readonly string gameUrl1 = "https://api.considition.com/";
        private static readonly string apiKey = "e67c67a6-ea90-4c18-9f07-a49f03c4da1c";
        private static readonly string mapFile = @"..\..\..\Map-Almhult.json";

        private static List<CustomerLoanRequestProposal> bestProposals = new();
        private static List<Dictionary<string, CustomerAction>> allIterations = new();

        static async Task Main(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            string mapDataText = File.ReadAllText(mapFile);
            MapData mapData = JsonConvert.DeserializeObject<MapData>(mapDataText);

            if (mapData?.customers == null || !mapData.customers.Any())
            {
                Console.WriteLine("No customers found in map data.");
                return;
            }

            List<CustomerLoanRequestProposal> validProposals = new();
            List<Customer> validCustomers = new(); 
            decimal totalLoanAmount = 0m; 

            List<(Customer customer, decimal bestProfit, CustomerLoanRequestProposal bestProposal, decimal loanAmount)> customerResults = new();

            var timer = new Stopwatch();
            timer.Start();
            Parallel.ForEach(mapData.customers,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async customer =>
            {
                Console.WriteLine($"Evaluating customer: {customer.name}");
                decimal bestProfit = decimal.MinValue;
                CustomerLoanRequestProposal bestProposal = null;
                decimal bestLoanAmount = 0m;

                for (int months = 5; months <= mapData.gameLengthInMonths; months++)
                {
                    decimal intrestcap = 0;

                    for (decimal rate = 0.01m; rate <= 0.3m; rate += 0.01m)
                    {
                        var input = new GameInput
                        {
                            MapName = mapData.name,
                            Proposals = new List<CustomerLoanRequestProposal>
                    {
                        new CustomerLoanRequestProposal
                        {
                            CustomerName = customer.name,
                            MonthsToPayBackLoan = months,
                            YearlyInterestRate = rate
                        }
                    },
                            Iterations = GenerateCustomerIterations(mapData, customer) 
                        };

                        Thread.Sleep(10);
                        var profit = SendRequestAndGetProfit(input);
                        profit.Wait();
                        if (profit.Result > bestProfit)
                        {
                            bestProfit = profit.Result;
                            bestProposal = input.Proposals.First();
                            bestLoanAmount = customer.loan.amount; 
                        }

                        if (intrestcap == 0 && profit.Result != 0)
                            intrestcap = profit.Result;

                        if (intrestcap != 0 && profit.Result == 0)
                            goto Foo;

                        Console.WriteLine($"Rate: {rate * 100}% for {months} months -> Profit: {profit.Result}");
                    }
                Foo: Console.WriteLine("Hej");
                }

                if (bestProfit > 0 && bestProposal != null)
                {
                    customerResults.Add((customer, bestProfit, bestProposal, bestLoanAmount));
                    Console.WriteLine($"Best result for {customer.name}: {bestProposal.YearlyInterestRate * 100}% for {bestProposal.MonthsToPayBackLoan} months, Profit: {bestProfit}, Loan Amount: {bestLoanAmount}");
                }


            });

            timer.Stop();
            TimeSpan timeTaken = timer.Elapsed;
            string foo = "Time taken: " + timeTaken.ToString(@"m\:ss\.fff");
            Console.WriteLine(foo);

            var sortedCustomerResults = customerResults.OrderByDescending(x => x.bestProfit / x.loanAmount);

            foreach (var result in sortedCustomerResults)
            {
                if (totalLoanAmount + result.loanAmount <= mapData.budget)
                {
                    validProposals.Add(result.bestProposal);
                    validCustomers.Add(result.customer);
                    totalLoanAmount += result.loanAmount;
                    Console.WriteLine($"Adding {result.customer.name} with Loan Amount: {result.loanAmount} With profit to loan ratio {result.bestProfit / result.loanAmount} with intrest {result.bestProposal.YearlyInterestRate}");

                }
                else
                {
                    Console.WriteLine($"Skipping customer {result.customer.name} due to budget constraints.");
                }
                Console.WriteLine(totalLoanAmount);
            }


            var customerIterations = GenerateAllCustomerIterations(mapData, validCustomers);
            allIterations.AddRange(customerIterations);
            if (validProposals.Any())
            {
                var gameInput = new GameInput
                {
                    MapName = mapData.name,
                    Proposals = validProposals,
                    Iterations = allIterations 
                };

                await SendProposalsToServer(gameInput);
            }
        }

        private static List<Dictionary<string, CustomerAction>> GenerateCustomerIterations(MapData mapData, Customer customer)
        {
            var iterations = new List<Dictionary<string, CustomerAction>>();

            for (int i = 0; i < mapData.gameLengthInMonths; i++)
            {
                var actions = new Dictionary<string, CustomerAction>
                {
                    { customer.name, new CustomerAction { Type = (i % 4 == 0) ? "Award" : "Skip", Award = (i % 4 == 0) ? "IkeaFoodCoupon" : "None" } }
                };
                iterations.Add(actions);
            }

            return iterations;
        }

        private static List<Dictionary<string, CustomerAction>> GenerateAllCustomerIterations(MapData mapData, List<Customer> validCustomers)
        {
            var iterations = new List<Dictionary<string, CustomerAction>>();
            var awards = new List<string> { "IkeaFoodCoupon" };

            for (int i = 0; i < mapData.gameLengthInMonths; i++)
            {
                var actions = new Dictionary<string, CustomerAction>();

                foreach (var cust in validCustomers) 
                {
                    actions[cust.name] = new CustomerAction
                    {
                        Type = (i % 4 == 0) ? "Award" : "Skip",
                        Award = (i % 4 == 0) ? awards[(i / 4) % awards.Count] : "None" 
                    };
                }

                iterations.Add(actions);
            }

            return iterations;
        }

        private static async Task<decimal> SendRequestAndGetProfit(GameInput input)
        {
            using HttpClient client = new();
            client.BaseAddress = new Uri(gameUrl);

            HttpRequestMessage request = new()
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri(gameUrl + "game"),
                Content = new StringContent(JsonConvert.SerializeObject(input), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", apiKey);

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<GameResult>(body);

                client.Dispose();
                return result.score.totalScore;
            }
            else
            {
                Console.WriteLine($"Error: {response.StatusCode}");
                return 0;
            }
        }

        private static async Task SendProposalsToServer(GameInput input)
        {
            Console.WriteLine(JsonConvert.SerializeObject(input));
            HttpClient client = new();
            client.BaseAddress = new Uri(gameUrl, UriKind.Absolute);
            HttpRequestMessage request = new();
            request.Method = HttpMethod.Post;
            request.RequestUri = new Uri(gameUrl + "game", UriKind.Absolute);
            request.Headers.Add("x-api-key", apiKey);
            request.Content = new StringContent(JsonConvert.SerializeObject(input), Encoding.UTF8, "application/json");

            var res = client.Send(request);
            Console.WriteLine(res.StatusCode);
            Console.WriteLine(await res.Content.ReadAsStringAsync());
        }


    }
}

public class MapData
{
    public string name { get; set; }
    public decimal budget { get; set; }
    public int gameLengthInMonths { get; set; }
    public List<Customer> customers { get; set; }
}

public class Customer
{
    public string name { get; set; }
    public decimal capital { get; set; }
    public decimal income { get; set; }
    public decimal monthlyExpenses { get; set; }
    public int numberOfKids { get; set; }
    public bool hasStudentLoan { get; set; }
    public decimal homeMortgage { get; set; }
    public Loan loan { get; set; }
    public string personality { get; set; }
}

public class Loan
{
    public decimal amount { get; set; }
}

public class GameInput
{
    public string MapName { get; set; }
    public List<CustomerLoanRequestProposal> Proposals { get; set; }
    public List<Dictionary<string, CustomerAction>> Iterations { get; set; }
}

public class CustomerLoanRequestProposal
{
    public string CustomerName { get; set; }
    public int MonthsToPayBackLoan { get; set; }
    public decimal YearlyInterestRate { get; set; }
}

public class CustomerAction
{
    public string Type { get; set; }
    public string Award { get; set; }
}

public class GameResult
{
    public Score score { get; set; }

}

public class Score
{
    public decimal totalScore { get; set; }

    public decimal totalProfit { get; set; }

    public decimal environmentalImpact { get; set; }


}