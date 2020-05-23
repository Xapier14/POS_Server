using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Data.OleDb;
using System.Data.SQLite;
using System.IO;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using SHS;
/*
* Project Info:
*  Project Name: POS Server
*  Date Started: 9/22/2018
*  Update: 03/20/2020
*  Written by: Lance Crisang
*  
*  ChangeLog:
*      [11/22/2018] Added Initial RSA Authentication for POSClients, Fixed listorders command
*      [11/17/2019] Complete overhaul of networking code. Removed implementations of faulty encryption.
*      [03/20/2020] Added HTTP Server for downloading of resources. Removed console spam due to unwanted connections.
*  
*  Notes:
*      Added SQLite for Local Private Database Access.
*      
*      Full Support for SQLite.
*      Full Support for MS SQL Server. (May not be updated to work with Ms SQL Server.)
*/
namespace POS_Server
{
    class Program
    {
        public static string DISC = "Development Version: 03/20/2020";
        public static int POSClients = 0, INVClients = 0, ServerMode = 0; //FORCE REMOTE SQL, SET SERVERMODE=0 WHEN RELEASING PUBLIC BUILD
        public static bool ExitFlag = false, AcceptedPOS = false, AcceptedINV = false, EchoUnknownMsg = false;
        public static TcpListener invlistener = new TcpListener(IPAddress.Any, 32546);
        public static TcpClient invclient = new TcpClient();
        public static TcpListener poslistener = new TcpListener(IPAddress.Any, 32545);
        public static TcpClient posclient = new TcpClient();
        public static TcpListener poselistener = new TcpListener(IPAddress.Any, 32544);
        public static TcpClient poseclient = new TcpClient();
        public static string Version = Assembly.GetEntryAssembly().GetName().Version.ToString();
        public static OleDbConnection DBFile = Database.ConnectToDB();
        public static IList<TcpClient> ClientList = new List<TcpClient>();
        public static IDictionary<TcpClient, int> TimeoutTable = new Dictionary<TcpClient, int>();
        public static int PollTimeoutFreq = 200, MaxTimeout = 5000, AuthFails = 0;
        public static int PacketSize = 128;
        public static SimpleHTTPServer server;
        public static string HTTPResources = Environment.CurrentDirectory + @"\hostedresources";

        public static void PrintInfo()
        {
            Console.WriteLine("[Info] POS Server, Version {0}. Private Build, For Demonstration Purposes Only.", Version, ServerMode);
            Console.WriteLine("[Info] DO NOT USE COMMERCIALLY!");
            Console.WriteLine("[Info] Server written by: Lance Crisang.");
            if (EchoUnknownMsg)
            {
                Console.WriteLine("[Warn] Unknown packets are currently being echoed.\n" +
                                  "[Warn] Please disable as this can lead to vulnerabilities.");
            }
            Console.WriteLine();
        }
        public static int CheckDependencies()
        {
            int S = 0;
            if (!File.Exists(@"System.Data.SQLite.dll"))
            {
                S += 1;
            }
            if (!File.Exists("SQLite.Interop.dll") && !File.Exists(@"x86\SQLite.Interop.dll") && !File.Exists(@"x64\SQLite.Interop.dll"))
            {
                S += 2;
            }
            return S;
        }
        static void Main(string[] args)
        {
            int Dep = CheckDependencies();
            if (Dep != 0)
            {
                switch (Dep)
                {
                    case 1:
                        Console.WriteLine("[Server] System cannot find 'System.Data.SQLite.dll'. It may have been deleted or moved to another location.");
                        break;
                    case 2:
                        Console.WriteLine("[Server] System cannot find 'SQLite.Interop.dll'. It may have been deleted or moved to another location.");
                        break;
                    case 3:
                        Console.WriteLine("[Server] System cannot find 'System.Data.SQLite.dll'. It may have been deleted or moved to another location.");
                        Console.WriteLine("[Server] System cannot find 'SQLite.Interop.dll'. It may have been deleted or moved to another location.");
                        break;
                }
                Environment.Exit(1);
            }
            if (args.Length > 1)
            {
                ServerMode = 1;
                if (args[0].ToLower() == "constring")
                {
                    Database.UseCustomConnectionString = true;
                } else
                {
                    Database.ServerName = args[0];
                }
                if (args.Length > 2)
                {
                    if (!Database.UseCustomConnectionString)
                    {
                        Database.DatabaseName = args[1];
                        if (args.Length > 3)
                        {
                            Database.ServerProvider = args[2];
                        }
                    } else
                    {
                        Database.ConnectionString = args[1];
                    }
                }
            }
            PrintInfo();
            if (DISC != String.Empty)
            {
                Console.WriteLine("[!MSG!] {0}\n", DISC.Replace("\n", "\n[!MSG!] "));
            }
            Database.CheckSQLConnection();
            /*
             * START WEBSERVER HERE
             * RUN IT AT PORT 32547
             * USE A LIGHT WEIGHT HTTPSERVER LIKE LIGHTTPD.
             */
            if (!Directory.Exists(HTTPResources))
            {
                Directory.CreateDirectory(HTTPResources);
            }
            if (!File.Exists(HTTPResources + @"\index.html"))
            {
                File.WriteAllText(HTTPResources + @"\index.html", "<html><title> POS Server </title><body>POS Server Resource Bin.</body></html>");
            }
            foreach (KeyValuePair<int, int> kp in POSSys.GetProductIDList())
            {
                if (!File.Exists(HTTPResources + @"\" + kp.Value.ToString() + ".png"))
                {
                    Tools.Log("File missing! (" + kp.Value.ToString() + ".png)!", "Resources");
                    //Console.WriteLine("[Resources] Warning! Item {0} has missing resources! ({1})", kp.Value, kp.Value.ToString() + ".png");
                }
            }
            server = new SimpleHTTPServer(HTTPResources, 32547);
            Console.WriteLine("[Server] HTTP Server running on 32547");
            Thread CommandListener = new Thread(new ThreadStart(AcceptCommands));
            Thread POSListenThread = new Thread(new ThreadStart(POSListenerThread));
            Thread INVListenThread = new Thread(new ThreadStart(INVListenerThread));
            POSListenThread.Start();
            //INVListenThread.Start();

            Thread.Sleep(300); //Wait for threads to start...
            CommandListener.Start();
            while (!ExitFlag)
            {
                //Keep updating the timeout table for connected clients until the ExitFlag has been raised.
                Thread.Sleep(PollTimeoutFreq);
                ClientTimeoutPoll();
            }
            poslistener.Stop();
            invlistener.Stop();
            server.Stop();
        }
        private static void POSListenerThread()
        {
            poslistener.Start();
            Console.WriteLine("[Server] Started listening for POSClient on Port 32545.");
            while (!ExitFlag) //Keep listening for connections until ExitFlag has been raised.
            {
                posclient = poslistener.AcceptTcpClient();
                //Run POSThreadProc in a new thread with the respective client passed.
                ThreadPool.QueueUserWorkItem(POSThreadProc, posclient);
            }
        }
        public static void ClientTimeoutPoll()
        {
            List<TcpClient> LRemove = new List<TcpClient>(); //Clients to remove
            foreach (TcpClient TC in ClientList)
            {
                if (TC.Connected)
                {
                    TimeoutTable.TryGetValue(TC, out int T_Val);
                    if (T_Val > 0)
                    {
                        //Update Timeout Counter for Client
                        T_Val -= PollTimeoutFreq;
                        if (T_Val < 0) T_Val = 0;
                        TimeoutTable.Remove(TC);
                        TimeoutTable.Add(TC, T_Val);
                    }
                    else
                    {
                        //Add client to the "to be removed list" as the timer ran out.
                        TC.Close();
                        LRemove.Add(TC);
                        TimeoutTable.Remove(TC);
                    }
                } else
                {
                    //Client not connected, force remove.
                    TC.Close();
                    LRemove.Add(TC);
                    TimeoutTable.Remove(TC);
                }
            }
            foreach(TcpClient TT in LRemove) //Remove each client in the client list if it is present in the remove list.
            {
                if (ClientList.Contains(TT))
                {
                    ClientList.Remove(TT);
                }
            }
        }
        private static void INVListenerThread()
        {
            invlistener.Start();
            Console.WriteLine("[Server] Started listening for InventoryManager on Port 32546.");
            while (!ExitFlag)
            {
                invclient = invlistener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(INVThreadProc, invclient);
            }
        }
        private static void POSThreadProc(object obj)
        {
            var client = (TcpClient)obj;
            int _PacketSize = PacketSize;
            ClientList.Add(client);
            TimeoutTable.Add(client, MaxTimeout);
            //Add Client to the client table and timeout table.

            if (client.Connected) ++POSClients; //If it had connected successfully, increment the number of clients.
            while (client.Connected)
            {
                //POS Client Work
                NetworkStream NS = client.GetStream();
                try
                {
                    if (NS.DataAvailable)
                    {
                        byte[] buffer = new byte[_PacketSize];
                        NS.Read(buffer, 0, _PacketSize); //Store the data into byte[] buffer if data is available.
                        if (buffer[0] != 127) //Packet[0], if command header is not 127, ignore and print to console for debug purposes.
                        {
                            Tools.Log(Tools.ByteArrayToString(buffer), "UnknownReceive");
                            if (EchoUnknownMsg) Console.Write("\n[Warn] Unknown connection alert!\n" +
                                            "[Warn] Packet Message: " + Encoding.Default.GetString(buffer) + "\n|Server:| ");
                        }
                        else
                        {
                            //if the command header is 127, check for second byte and execute functions based on this.

                            switch (buffer[1]) //Check second byte in packet
                            {
                                case 1: //Misc Functions, If second byte = 1;
                                    switch (buffer[2])
                                    {
                                        case 1: //Timeout Counter Reset
                                            TimeoutTable.Remove(client);
                                            TimeoutTable.Add(client, MaxTimeout);
                                            break;
                                        case 2: //Changed Packet Size
                                            int old = _PacketSize;
                                            _PacketSize = BitConverter.ToInt32(buffer, 3);
                                            Tools.Log("Changed Packet Size. (Old: " + old.ToString() + ", New: " + _PacketSize.ToString() + ")", "POSListener");
                                            break;
                                    }
                                    break;
                                case 2: //POS Functions, If second byte = 2;
                                    switch (buffer[2])
                                    {
                                        case 1: //Send Order = 127, 2, 1, [3-6 = ProdID], [7-10 = Amount], [11-14, OrderGroup]
                                            POSSys.SendOrder(BitConverter.ToInt32(buffer, 3), BitConverter.ToInt32(buffer, 3 + 4), BitConverter.ToInt32(buffer, 3 + 8));
                                            Tools.Log("Order request received!", "POSListener");
                                            break;
                                        case 2: //Send Product List
                                            Tools.Log("Product List request received! Retrieving Product List from DB...", "POSListener");
                                            string spl_data = "";
                                            foreach (KeyValuePair<int, object> kp in POSSys.GetProductList())
                                            {
                                                spl_data = spl_data + (string)kp.Value + "\n";
                                            }
                                            spl_data.Remove(spl_data.Length - 2, 2);
                                            Tools.Log("Retrieved Data!", "POSListener");
                                            byte[] spl_rdata = Encoding.ASCII.GetBytes(spl_data);
                                            Tools.Log("Sending Data Length (" + spl_rdata.Length.ToString() + ")...", "POSListener");
                                            NS.Write(BitConverter.GetBytes(spl_rdata.Length), 0, 4);
                                            Tools.Log("Sending Data...", "POSListener");
                                            NS.Write(spl_rdata, 0, spl_rdata.Length);
                                            Tools.Log("Sent Data!", "POSListener");
                                            break;
                                        case 3: //Send Product Prices
                                            Tools.Log("Product Price List request received! Retrieving Price List from DB...", "POSListener");
                                            IDictionary<int, int> spp_pidlist = POSSys.GetProductIDList();
                                            int[] spp_data = new int[spp_pidlist.Count];
                                            for (int i = 0; i < spp_pidlist.Count; ++i)
                                            {
                                                int pid = -1;
                                                try
                                                {
                                                    spp_pidlist.TryGetValue(i, out pid);
                                                    spp_data[i] = POSSys.GetPrice(pid);
                                                }
                                                catch (Exception ex)
                                                {
                                                    Tools.Log(ex.Message, "POSListener_Error (" +  i.ToString() + " = " + pid.ToString() + ")]");
                                                }
                                            }
                                            Tools.Log("Retrieved Data!", "POSListener");
                                            byte[] spp_rdata = Tools.Int32ArrayToByteArray(spp_data);
                                            Tools.Log("Sending Data Length (" + spp_rdata.Length.ToString() + ")...", "POSListener");
                                            NS.Write(BitConverter.GetBytes(spp_rdata.Length), 0, 4);
                                            Tools.Log("Sending Data...", "POSListener");
                                            NS.Write(spp_rdata, 0, spp_rdata.Length);
                                            Tools.Log("Sent Data!", "POSListener");
                                            break;
                                        case 4: //Send Product Type List
                                            Tools.Log("Product Type List request received! Retrieving Product Type List from DB...", "POSListener");
                                            string spt_data = "";
                                            Tools.Log("Types: " + POSSys.GetTypeList().Count.ToString(), "POSListener");
                                            foreach (KeyValuePair<int, object> kp in POSSys.GetTypeList())
                                            {
                                                spt_data = spt_data + (string)kp.Value + "\n";
                                            }
                                            spt_data.Remove(spt_data.Length - 2, 2);
                                            Tools.Log("Retrieved Data!", "POSListener");
                                            byte[] spt_rdata = Encoding.ASCII.GetBytes(spt_data);
                                            Tools.Log("Sending Data Length (" + spt_rdata.Length.ToString() + ")...", "POSListener");
                                            NS.Write(BitConverter.GetBytes(spt_rdata.Length), 0, 4);
                                            Tools.Log("Sending Data...", "POSListener");
                                            NS.Write(spt_rdata, 0, spt_rdata.Length);
                                            Tools.Log("Sent Data!", "POSListener");
                                            break;
                                        case 5: //Recommend New OrderGroup ID
                                            Random rng = new Random();
                                            while (true)
                                            {
                                                List<int> ordergroups = POSSys.GetOrderGroups();
                                                int maxval = 65535;
                                                int newog = rng.Next(0, maxval);
                                                if (!ordergroups.Contains(newog))
                                                {
                                                    byte[] pack = BitConverter.GetBytes(newog);
                                                    NS.Write(pack, 0, pack.Length);
                                                    Tools.Log($"Recommended ordergroup id ({newog}) to client.", "POSListener");
                                                    break;
                                                }
                                            }
                                            break;
                                    }
                                    break;
                            }
                        }
                    }
                } catch (Exception ex)
                {
                    Tools.Log(ex.Message, "POSListener_HighException");
                }
            }
            POSClients--;
        }
        private static void INVThreadProc(object obj)
        {
            var client = (TcpClient)obj;
            INVClients++;
            while (client.Connected)
            {
                //INV Client Work
                //To do: Inventory Client
            }
            INVClients--;
        }
        private static void AcceptCommands()
        {
            while (true)
            {
                Console.Write("|Server:| ");
                string Command = Console.ReadLine();
                if (AuthFails > 0)
                {
                    Console.WriteLine("[Server_ConnAuth] Auth fails: {0}.", AuthFails);
                    AuthFails = 0;
                }
                ParseCommand(Command);
            }
        }
        public static void SwitchServerMode()
        {
            if (ServerMode != 0)
            {
                ServerMode = 0;
            } else
            {
                ServerMode = 1;
            }
            Database.CheckSQLConnection();
        }
        private static void ParseCommand(string Command)
        {
            switch (Command.ToLower())
            {
                case "help":
                    Console.WriteLine("\n[Help] Commands Available:\n" +
                        "[Help] help - Displays available commands for this server app.\n" +
                        "[Help] poshelp - Displays POS related commands.\n" +
                        "[Help] threadcount - Get process thread count.\n" +
                        "[Help] clientcount - Get client count.\n" +
                        "[Help] senddb - Send SQL Commands.\n" +
                        "[Help] info - Display server info.\n" +
                        "[Help] clear - Clears console.\n" +
                        "[Help] exit - Stop server & exits app.\n" +
                        "[Help] openlog - Opens server log.\n" +
                        "[Help] clearlog - Clears server log.\n" +
                        "[Help] switchservermode - Switches server mode.");
                    break;
                case "poshelp":
                    Console.WriteLine("\n[Help] POSCommands Available:\n" +
                        "[Help] toggleordercomplete - Toggle order complete status.\n" +
                        "[Help] getprice - Get product price from product id.\n" +
                        "[Help] getordergroupprice - Get ordergroup total price from ordergroup id.\n" +
                        "[Help] sendorder - Send an order to the order table.\n" +
                        "[Help] deleteorder - Deletes an order.\n" +
                        "[Help] clearordertable - Clears the order table.\n" +
                        "[Help] deleteordergroup - Deletes an ordergroup.\n" +
                        "[Help] listproducts - Lists products available from menu table.\n" +
                        "[Help] listorders - Lists orders assigned from ordergroup id.\n" +
                        "[Help]\n" +
                        "[Help] POSClient Listener on Port 32545.\n" +
                        "[Help] InventoryManager Listener on Port 32546.");
                    break;
                case "exit":
                    ExitFlag = true;
                    Console.WriteLine("[Server] Stopping...");
                    posclient.Close();
                    invclient.Close();
                    poslistener.Stop();
                    invlistener.Stop();
                    Environment.Exit(0);
                    break;
                case "threadcount":
                    Console.WriteLine("[Server] Thread Count: {0}", Process.GetCurrentProcess().Threads.Count);
                    break;
                case "clear":
                    Console.Clear();
                    break;
                case "clientcount":
                    Console.WriteLine("[Server] POS Clients: {0}, InventoryManager Clients: {1}", POSClients, INVClients);
                    break;
                case "info":
                    PrintInfo();
                    break;
                case "senddb":
                    Console.Write("|SQL_Query:| ");
                    string dbcom = Console.ReadLine();
                    Console.WriteLine(Database.SendCommand(dbcom));
                    break;
                case "clearlog":
                    if (File.Exists("log.txt"))
                    {
                        File.WriteAllText("log.txt", "");
                    }
                    else
                    {
                        Console.WriteLine("[Server] Log file does not exist.");
                    }
                    break;
                case "openlog":
                    if(File.Exists("log.txt"))
                    {
                        Process.Start("log.txt");
                    } else
                    {
                        Console.WriteLine("[Server] Log file does not exist.");
                    }
                    break;
                case "getprice":
                    Console.Write("|ProductID:| ");
                    string Price_ProdID = Console.ReadLine();
                    string Price_ProdName = POSSys.GetProduct(Convert.ToInt32(Regex.Replace(Price_ProdID, @"[^\d]", "")));
                    if (Price_ProdName != String.Empty)
                    {
                        Console.WriteLine("[POSSys] Price of {0}: " + POSSys.GetPrice(Convert.ToInt32(Regex.Replace(Price_ProdID, @"[^\d]", ""))), Price_ProdName);
                    } else
                    {
                        Console.WriteLine("[POSSys] Product not found or not defined!");
                    }
                    break;
                case "getordergroupprice":
                    Console.Write("|OrderGroup:| ");
                    string OrderGroupPrice_OrderGroup = Console.ReadLine();
                    if (OrderGroupPrice_OrderGroup != String.Empty)
                    {
                        int OrderGroupPrice_Total = POSSys.GetOrderGroupTotalPrice(Convert.ToInt32(Regex.Replace(OrderGroupPrice_OrderGroup, @"[^\d]", "")));
                        if (OrderGroupPrice_Total > -1)
                        {
                            Console.WriteLine("[POSSys] Total Price for OrderGroup #" + OrderGroupPrice_OrderGroup + ": {0}.", OrderGroupPrice_Total);
                        }
                        else
                        {
                            Console.WriteLine("[POSSys] OrderGroup does not exist or is deleted!");
                        }
                    }
                    else
                    {
                        Console.WriteLine("[POSSys] OrderGroup is invalid!");
                    }
                    break;
                case "toggleordercomplete":
                    Console.Write("|OrderID:| ");
                    string TogOrCom_OrderID = Console.ReadLine();
                    string Sani_TogOrCom_OrdId = Regex.Replace(TogOrCom_OrderID, @"[^\d]", "");
                    POSSys.SetOrderComplete(Convert.ToInt32(Sani_TogOrCom_OrdId), !POSSys.GetOrderComplete(Convert.ToInt32(Sani_TogOrCom_OrdId)));
                    Console.WriteLine("[POSSys] OrderID #" + Sani_TogOrCom_OrdId + " set to " + POSSys.GetOrderComplete(Convert.ToInt32(Sani_TogOrCom_OrdId)).ToString() + ".");
                    break;
                case "sendorder":
                    Console.Write("|ProductID:| ");
                    string SendOrder_ProdID = Console.ReadLine();
                    Console.Write("|Amount:| ");
                    string SendOrder_Amount = Console.ReadLine();
                    Console.Write("|OrderGroup:| ");
                    string SendOrder_OrderGroup = Console.ReadLine();
                    string Sani_SendOrder_ProdID = Regex.Replace(SendOrder_ProdID, @"[^\d]", "");
                    string Sani_SendOrder_Amount = Regex.Replace(SendOrder_Amount, @"[^\d]", "");
                    string Sani_SendOrder_OrderGroup = Regex.Replace(SendOrder_OrderGroup, @"[^\d]", "");
                    POSSys.SendOrder(Convert.ToInt32(Sani_SendOrder_ProdID), Convert.ToInt32(Sani_SendOrder_Amount), Convert.ToInt32(Sani_SendOrder_OrderGroup));
                    int SendOrder_OrderID = Convert.ToInt32(Database.GetDataFromSQL("SELECT ID FROM dbo.POS_OrderTable WHERE PRODUCTID=" + Sani_SendOrder_ProdID + " AND AMOUNT=" + Sani_SendOrder_Amount + " AND ORDERGROUP=" + Sani_SendOrder_OrderGroup, Database.DataType_Int32));
                    int SendOrder_ProductPrice = Convert.ToInt32(Database.GetDataFromSQL("SELECT PRICE FROM dbo.POS_MenuTable WHERE ID=" + Sani_SendOrder_ProdID, Database.DataType_Int32));
                    object SendOrder_ProductName_obj = Database.GetDataFromSQL("SELECT PRODUCT FROM dbo.POS_MenuTable WHERE ID=" + Sani_SendOrder_ProdID, Database.DataType_String);
                    string SendOrder_ProductName = "";
                    if (SendOrder_ProductName_obj != null)
                    {
                        SendOrder_ProductName = SendOrder_ProductName_obj.ToString();
                        Console.WriteLine("[POSSys] Placed Order #" + SendOrder_OrderID.ToString() + ", OrderGroup #" + Sani_SendOrder_OrderGroup + " - " + Sani_SendOrder_Amount + "x " + SendOrder_ProductName + "[" + Sani_SendOrder_ProdID + "]. Price: " + (SendOrder_ProductPrice * Convert.ToInt32(Sani_SendOrder_Amount)).ToString());
                    } else
                    {
                        Console.WriteLine("[POSSys] Product not found or not defined!");
                    }
                    break;
                case "clearordertable":
                    POSSys.ClearOrderTable();
                    Console.WriteLine("[POSSys] Cleared Order Table.");
                    break;
                case "switchservermode":
                    SwitchServerMode();
                    Console.WriteLine("[Server] Server mode now set to {0}.", ServerMode);
                    break;
                case "deleteorder":
                    Console.Write("|OrderID:| ");
                    string DeleteOrder_OrderID = Console.ReadLine();
                    string Sani_DeleteOrder_OrderID = Regex.Replace(DeleteOrder_OrderID, @"[^\d]", "");
                    bool DeleteOrder_DeleteFail = POSSys.DeleteOrder(Convert.ToInt32(Sani_DeleteOrder_OrderID));
                    if (!DeleteOrder_DeleteFail)
                    {
                        Console.WriteLine("[POSSys] Deleted Order #" + Sani_DeleteOrder_OrderID + " on OrderGroup #" + POSSys.GetOrderGroup(Convert.ToInt32(Sani_DeleteOrder_OrderID)).ToString() + ".");
                    }
                    else
                    {
                        Console.WriteLine("[POSSys] OrderID not found!");
                    }
                    break;
                case "deleteordergroup":
                    Console.Write("|OrderGroup:| ");
                    string DeleteOrderGroup_OrderGroup = Console.ReadLine();
                    string Sani_DeleteOrderGroup_OrderGroup = Regex.Replace(DeleteOrderGroup_OrderGroup, @"[^\d]", "");
                    bool DeleteOrderGroup_DeleteFail = POSSys.DeleteOrderGroup(Convert.ToInt32(Sani_DeleteOrderGroup_OrderGroup));
                    if (!DeleteOrderGroup_DeleteFail)
                    {
                        Console.WriteLine("[POSSys] Deleted OrderGroup #" + Sani_DeleteOrderGroup_OrderGroup + ".");
                    }
                    else
                    {
                        Console.WriteLine("[POSSys] OrderGroup not found!");
                    }
                    break;
                case "listproducts":
                    IDictionary<int, object> ProdList = POSSys.GetProductList();
                    int d_i = 0;
                    object prod_data = null;
                    Console.WriteLine("[POS] Product List:");
                    while (d_i < ProdList.Count)
                    {
                        ProdList.TryGetValue(d_i, out prod_data);
                        Console.WriteLine("[POS] {0}) {1}, Price = {2}.", (d_i + 1).ToString(), prod_data.ToString(), POSSys.GetPrice(d_i + 1).ToString());
                        d_i++;
                    }
                    break;
                case "listorders":
                    Console.Write("|OrderGroup:| ");
                    string ListOrders_OrderGroup = Console.ReadLine();
                    int o_i = 0, orderproduct_data = 0, ListOrders_OD = Convert.ToInt32(Tools.SanitizeIntString(ListOrders_OrderGroup));
                    IDictionary<int, object> OrderList = POSSys.GetProductList(ListOrders_OD);
                    IDictionary<int, int> OrderProdList = POSSys.GetProductIDList(ListOrders_OD);
                    IDictionary<int, object> OrderIDlist = POSSys.GetOrderListFromOrderGroup(ListOrders_OD); //Returns OrderID list from OrderGroup
                    object order_data = null;
                    Console.WriteLine("[POS] Order List for OrderGroup #{0}:", ListOrders_OD);
                    while (o_i < OrderList.Count)
                    {
                        OrderList.TryGetValue(o_i, out order_data);
                        OrderProdList.TryGetValue(o_i, out orderproduct_data);
                        Console.WriteLine("[POS] {0}) {1} x{2} - Price: {3}",
                            (o_i + 1).ToString(),
                            POSSys.GetProduct(OrderProdList[o_i]), 
                            POSSys.GetAmount(Convert.ToInt32(OrderIDlist[o_i])), 
                            POSSys.GetTotalPrice(Convert.ToInt32(OrderIDlist[o_i]))
                            );
                        o_i++;
                    }
                    break;
                default:
                    if (Command != String.Empty)
                    {
                        Console.WriteLine("[Server] Command not recognized.");
                    }
                    break;
            }
        }
    }
    class Tools
    {
        public static byte[] DeleteRestArray(byte[] Array, int DeleteIndexStart)
        {
            byte[] ret = new byte[DeleteIndexStart];
            for (int i = 0; i < DeleteIndexStart; i++)
            {
                ret[i] = Array[i];
            }
            return ret;
        }
        public static string ByteArrayToString(byte[] Array)
        {
            string ret = "";
            foreach (byte data in Array)
            {
                ret = ret + " " + data.ToString();
            }
            ret = ret.Remove(0, 1);
            return ret;
        }
        public static byte[] CopyArray(byte[] Source, int IndexSource)
        {
            if (!(IndexSource < Source.Length)) return new byte[0];
            byte[] ret = new byte[Source.Length - IndexSource];
            for (int i = 0; i < ret.Length; i++)
            {
                ret[i] = Source[i + IndexSource];
            }
            return ret;
        }
        public static string SanitizeIntString(string str)
        {
            string ret = "";
            ret = Regex.Replace(str, @"[^\d]", "");
            return ret;
        }
        public static ulong GetTotalSeconds()
        {
            return Convert.ToUInt64((DateTime.Today.Year * 12 * 31 * 24 * 60 * 60) + (DateTime.Today.Month * 31 * 24 * 60 * 60) + (DateTime.Today.Day * 24 * 60 * 60) + (DateTime.Today.Hour * 60 * 60) + (DateTime.Today.Minute * 60) + (DateTime.Today.Second));
        }
        public static byte[] JoinByteArray(byte[] Array1, byte[] Array2)
        {
            byte[] ret = new byte[Array1.Length + Array2.Length];
            int i = 0;
            foreach (byte o1 in Array1)
            {
                ret[i] = o1;
                i++;
            }
            foreach (byte o2 in Array2)
            {
                ret[i] = o2;
                i++;
            }
            return ret;
        }
        public static byte[] AddFirstToByteArray(byte Byte, byte[] Array1)
        {
            byte[] ret = new byte[1 + Array1.Length];
            int i = 1;
            ret[0] = Byte;
            foreach (byte o1 in Array1)
            {
                ret[i] = o1;
                i++;
            }
            return ret;
        }
        public static byte[] Int32ArrayToByteArray(int[] Array)
        {
            byte[] ret = new byte[Array.Length * 4];
            int ii = 0;
            foreach (int i in Array)
            {
                byte[] iArray = BitConverter.GetBytes(i);
                ret[ii] = iArray[0];
                ret[ii + 1] = iArray[1];
                ret[ii + 2] = iArray[2];
                ret[ii + 3] = iArray[3];
                ii += 4;
            }
            return ret;
        }
        public static void Log(string Msg, string Header)
        {
            StreamWriter FFS = File.AppendText("log.txt");
            FFS.WriteLine("\n[" + DateTime.Now.ToString() + "][" + Header +"] " + Msg);
            FFS.Close();
        }
    }
    class POSSys
    {
        public static bool SendOrder(int ProductID, int Amount, int OrderGroup)
        {
            bool Errors = true;
            string ProductName = "";
            object data = Database.GetDataFromSQL("SELECT PRODUCT FROM dbo.POS_MenuTable WHERE ID = " + ProductID.ToString(), Database.DataType_String);
            if (data != null)
            {
                ProductName = data.ToString();
                int TotalPrice = Convert.ToInt32(Database.GetDataFromSQL("SELECT PRICE FROM dbo.POS_MenuTable WHERE ID = " + ProductID.ToString(), Database.DataType_Int32)) * Amount;
                if (Database.SendCommand("INSERT INTO dbo.POS_OrderTable (PRODUCT, PRODUCTID, ORDERDATE, AMOUNT, TOTALPRICE, ORDERGROUP) VALUES ('" + ProductName + "', " + ProductID.ToString() + ", " + Database.GetDate() + ", " + Amount.ToString() + ", " + TotalPrice.ToString() + ", " + OrderGroup.ToString() + ")") != String.Empty)
                {
                    Errors = true;
                }
                else
                {
                    Errors = false;
                }
            }
            return Errors;
        }
        public static bool SetOrderComplete(int OrderID, bool Completed)
        {
            Database.SendCommand("UPDATE dbo.POS_OrderTable SET COMPLETED=" + Convert.ToInt32(Completed).ToString() + " WHERE ID=" + OrderID.ToString());
            return false;
        }
        public static bool GetOrderComplete(int OrderID)
        {
            bool OrderCompleted = false;
            OrderCompleted = Convert.ToBoolean(Database.GetDataFromSQL("SELECT COMPLETED FROM dbo.POS_OrderTable WHERE ID=" + OrderID.ToString(), Database.DataType_Bool));       
            return OrderCompleted;
        }
        public static int GetOrderGroup(int OrderID)
        {
            int ret = -1;
            object DATA = Database.GetDataFromSQL("SELECT ORDERGROUP FROM dbo.POS_OrderTable WHERE ID=" + OrderID.ToString(), Database.DataType_Int32);
            if (DATA != null)
            {
                ret = Convert.ToInt32(DATA);
            }
            return ret;
        }
        public static void ClearOrderTable()
        {
            Database.SendCommand("DELETE FROM dbo.POS_OrderTable");
            if (Program.ServerMode != 0)
            {
                Database.SendCommand("DBCC CHECKIDENT('dbo.POS_OrderTable', RESEED, 0)");
            }
        }
        public static IDictionary<int, int> GetProductIDList()
        {
            OleDbConnection Connection = Database.ConnectToDB();
            IDictionary<int, object> ID_Data = Database.GetListFromSQL("SELECT ID FROM dbo.POS_MenuTable", Database.DataType_Int32);
            IDictionary<int, int> result = new Dictionary<int, int>();
            int i = 0;
            foreach (KeyValuePair<int, object> Data in ID_Data)
            {
                result.Add(i, Convert.ToInt32(Data.Value));
                i++;
            }
            return result;
        }
        public static int GetProductID(int OrderID)
        {
            int pid = -1;
            object data = Database.GetDataFromSQL("SELECT PRODUCTID FROM dbo.POS_OrderTable WHERE ID=" + OrderID.ToString(), Database.DataType_Int32);
            pid = Convert.ToInt32(data);
            return pid;
        }
        public static IDictionary<int, int> GetProductIDList(int OrderGroup)
        {
            OleDbConnection Connection = Database.ConnectToDB();
            IDictionary<int, object> ProdID_Data = Database.GetListFromSQL("SELECT PRODUCTID FROM dbo.POS_OrderTable WHERE ORDERGROUP=" + OrderGroup, Database.DataType_Int32);
            IDictionary<int, int> result = new Dictionary<int, int>();
            int i = 0;
            object data = null;
            while (i < ProdID_Data.Count)
            {
                ProdID_Data.TryGetValue(i, out data);
                result.Add(i, Convert.ToInt32(data));
                i++;
            }
            return result;
        }
        public static IDictionary<int, int> GetAmountList(int OrderGroup)
        {
            OleDbConnection Connection = Database.ConnectToDB();
            IDictionary<int, object> ID_Data = Database.GetListFromSQL("SELECT AMOUNT FROM dbo.POS_OrderTable WHERE ORDERGROUP=" + OrderGroup, Database.DataType_Int32);
            IDictionary<int, int> result = new Dictionary<int, int>();
            int i = 0;
            foreach (object Data in ID_Data)
            {
                result.Add(i, Convert.ToInt32(Data));
                i++;
            }
            return result;
        }
        public static int GetAmount(int OrderID)
        {
            int Amt = 0;
            object data = Database.GetDataFromSQL("SELECT AMOUNT FROM dbo.POS_OrderTable WHERE ID=" + OrderID.ToString(), Database.DataType_Int32);
            Amt = Convert.ToInt32(data);
            return Amt;
        }
        public static IDictionary<int, int> GetOrderIDList()
        {
            OleDbConnection Connection = Database.ConnectToDB();
            IDictionary<int, object> ID_Data = Database.GetListFromSQL("SELECT ID FROM dbo.POS_OrderTable", Database.DataType_Int32);
            IDictionary<int, int> result = new Dictionary<int, int>();
            int i = 0;
            foreach (object Data in ID_Data)
            {
                result.Add(i, Convert.ToInt32(Data));
                i++;
            }
            return result;
        }
        public static string GetProduct(int ProductID)
        {
            OleDbConnection Connection = Database.ConnectToDB();
            string Product = "";
            object ProdData = Database.GetDataFromSQL("SELECT PRODUCT FROM dbo.POS_MenuTable WHERE ID=" + ProductID.ToString(), Database.DataType_String);
            try
            {
                Product = ProdData.ToString();
            } catch (Exception e)
            {
                //error get
                Console.WriteLine(e.Message);
            }
            return Product;
        }
        public static int GetPrice(int ProductID)
        {
            int Price = -1;
            object PriceData = Database.GetDataFromSQL("SELECT PRICE FROM dbo.POS_MenuTable WHERE ID=" + ProductID.ToString(), Database.DataType_Int32);
            Price = Convert.ToInt32(PriceData);
            return Price;
        }
        public static int GetTotalPrice(int OrderID)
        {
            int Price = -1;
            object PriceData = Database.GetDataFromSQL("SELECT TOTALPRICE FROM dbo.POS_OrderTable WHERE ID=" + OrderID.ToString(), Database.DataType_Int32);
            Price = Convert.ToInt32(PriceData);
            return Price;
        }
        public static int GetOrderGroupTotalPrice(int OrderGroup)
        {
            int Total = -1;
            IDictionary<int, object> TotalPriceList_obj = Database.GetListFromSQL("SELECT TOTALPRICE FROM dbo.POS_OrderTable WHERE DELETED=0 AND ORDERGROUP=" + OrderGroup.ToString(), Database.DataType_Int32);
            int i = 0;
            object data = null;
            while (i < TotalPriceList_obj.Count)
            {
                TotalPriceList_obj.TryGetValue(i, out data);
                if (data != null)
                {
                    if (Total < 0)
                    {
                        Total = 0;
                    }
                    Total += Convert.ToInt32(data);
                }
                i++;
            }
            return Total;
        }
        public static bool DeleteOrder(int OrderID)
        {
            bool Ret = false;
            object ORDERDATA = Database.GetDataFromSQL("SELECT DELETED FROM dbo.POS_OrderTable WHERE ID=" + OrderID.ToString(), Database.DataType_Bool);
            if (ORDERDATA != null)
            {
                if (!Convert.ToBoolean(ORDERDATA))
                {
                    //Delete Order
                    Database.SendCommand("UPDATE dbo.POS_OrderTable SET DELETED=1 WHERE ID=" + OrderID.ToString());
                }
            } else
            {
                Ret = true;
            }
            return Ret;
        }
        public static bool DeleteOrderGroup(int OrderGroup)
        {
            bool ret = true;
            object CHECKDATA = Database.GetDataFromSQL("SELECT PRODUCT FROM dbo.POS_OrderTable WHERE ORDERGROUP=" + OrderGroup.ToString(), Database.DataType_String);
            if (CHECKDATA != null)
            {
                ret = false;
                //OrderGroup Exists
                Database.SendCommand("UPDATE dbo.POS_OrderTable SET DELETED=1 WHERE ORDERGROUP=" + OrderGroup.ToString());
                /* 
                //SLOW METHOD
                IDictionary<int, object> DATAGROUP = Database.GetListFromSQL("SELECT ID FROM dbo.POS_OrderTable WHERE ORDERGROUP=" + OrderGroup.ToString(), Database.DataType_Int32);
                int KEY = 0;
                object DATA = null;
                while (KEY < DATAGROUP.Count)
                {
                    DATAGROUP.TryGetValue(KEY, out DATA);
                    POSSys.DeleteOrder(Convert.ToInt32(Tools.SanitizeIntString(Convert.ToString(DATA))));
                    KEY++;
                }
                */
            }
            return ret;
        }
        public static IDictionary<int, object> GetProductList()
        {
            IDictionary<int, object> Dict = Database.GetListFromSQL("SELECT PRODUCT FROM dbo.POS_MenuTable", Database.DataType_String);
            return Dict;
        }
        public static IDictionary<int, object> GetTypeList()
        {
            IDictionary<int, object> Dict = Database.GetListFromSQL("SELECT TYPE FROM dbo.POS_MenuTable", Database.DataType_String);
            return Dict;
        }
        public static IDictionary<int, object> GetMainOrderList()
        {
            IDictionary<int, object> Dict = Database.GetListFromSQL("SELECT PRODUCT FROM dbo.POS_OrderTable", Database.DataType_String);
            return Dict;
        }
        public static IDictionary<int, object> GetOrderListFromOrderGroup(int OrderGroup)
        {
            IDictionary<int, object> Dict = Database.GetListFromSQL("SELECT ID FROM dbo.POS_OrderTable WHERE ORDERGROUP=" + OrderGroup.ToString(), Database.DataType_Int32);
            return Dict;
        }
        public static IDictionary<int, object> GetProductList(int OrderGroup)
        {
            IDictionary<int, object> Dict = Database.GetListFromSQL("SELECT PRODUCT FROM dbo.POS_OrderTable WHERE ORDERGROUP=" + OrderGroup.ToString(), Database.DataType_String);
            return Dict;
        }
        public static IDictionary<int, object> GetProductAmountFromOrder(int InitialID, int OrderGroup)
        {
            IDictionary<int, object> Dict = Database.GetListFromSQL("SELECT AMOUNT FROM dbo.POS_OrderTable WHERE ORDERGROUP=" + OrderGroup.ToString(), Database.DataType_String);
            return Dict;
        }
        public static List<int> GetOrderGroups()
        {
            List<int> ret = new List<int>();
            IDictionary<int, object> dict = Database.GetListFromSQL("SELECT ORDERGROUP FROM dbo.POS_OrderTable", Database.DataType_Int32);
            foreach(KeyValuePair<int, object> kp in dict)
            {
                if (!ret.Contains((int)kp.Value))
                {
                    ret.Add((int)kp.Value);
                }
            }
            return ret;
        }
    }
    class Database
    {
        public const int DataType_String = 0;
        public const int DataType_Int16 = 1;
        public const int DataType_Int32 = 2;
        public const int DataType_DateTime = 3;
        public const int DataType_Bool = 4;
        public static bool UseCustomConnectionString = false;
        public static string ServerName = "", DatabaseName = "", ServerProvider = "SQLOLEDB", ConnectionString = "";

        public static void CheckSQLConnection()
        {
            if (Program.ServerMode == 1)
            {
                Console.WriteLine("[Server] SQL Server Connection Mode: RemoteSQL");
                Console.WriteLine("[SQL] Testing server connection...");
                string sql_verinfo = Database.SendCommand("SELECT @@VERSION");
                if (sql_verinfo == "")
                {
                    Console.WriteLine("[SQL] Connection failed!...");
                    Console.WriteLine("[Info] This mode requres an SQL Server with the correct database and table to operate.\n" +
                        "[Info] Please connect to the external server using 'posserver [SERVERNAME_INSTANCE] [DATABASE] [PROVIDER]' or\n" +
                        "[Info] use 'posserver constring [CONNECTIONSTRING]'\n" +
                        "[Info] Please refer to the documentation for setting up the correct tables.\n");
                }
                else
                {
                    Console.WriteLine("[SQL] Recieved version info!\n{0}", sql_verinfo);
                }
            }
            else
            {
                //SQLDatabase Test
                Console.WriteLine("[Server] SQL Server Connection Mode: Local SQLite");
                if (!File.Exists("localdatabase.sqlite"))
                {
                    Console.WriteLine("[SQLite_Init] Creating database...");
                    SQLiteConnection.CreateFile("localdatabase.sqlite");
                    if (File.Exists("posdatabase.sqlite"))
                    {
                        File.Delete("posdatabase.sqlite");
                    }
                    SQLiteConnection.CreateFile("posdatabase.sqlite");
                    Console.WriteLine("[SQLite_Init] Initializing tables...");
                    Database.InitializeSQLiteDB();
                    //INITIALIZE DATABASE HERE!
                }
                Console.WriteLine("[SQLite_Init] Testing connection...");
                string sql_verinfo = Database.SendCommand("SELECT sqlite_version()");
                if (sql_verinfo == "")
                {
                    Console.WriteLine("[SQLite_Init] Connection failed!...");
                }
                else
                {
                    Console.WriteLine("[SQLite_Init] Connection verified! Version: SQLite {0}.", sql_verinfo);

                }
            }
        }
        public static OleDbConnection ConnectToDB()
        {
            //OleDbConnection DB = new OleDbConnection(@"Provider=Microsoft.ACE.OLEDB.12.0;Data Source =database.accdb"); //Connection String for MS Access Database
            OleDbConnection DB;
            if (!UseCustomConnectionString)
            {
                DB = new OleDbConnection(@"Provider=" + ServerProvider + ";Data Source=" + ServerName + ";Initial Catalog=" + DatabaseName + ";Integrated Security=SSPI");
            }
            else
            {
                DB = new OleDbConnection(@ConnectionString);
            }
            return DB;
        }
        public static string GetDate()
        {
            string ret = "datetime()";
            if (Program.ServerMode != 0)
            {
                ret = "getdate()";
            }
            return ret;
        }
        public static void InitializeSQLiteDB()
        {
            if (Program.ServerMode == 0)
            {
                string init1_res = Database.SendCommand("CREATE TABLE dbo.POS_OrderTable (PRODUCT TEXT, PRODUCTID INTEGER, COMPLETED INTEGER DEFAULT 0, ORDERDATE TEXT, AMOUNT INTEGER, TOTALPRICE INTEGER, ID INTEGER PRIMARY KEY, DELETED INTEGER DEFAULT 0, ORDERGROUP INTEGER NOT NULL);");
                if (init1_res != "")
                {
                    Console.WriteLine("[SQLite] {0}", init1_res);
                }
                string init2_res = Database.SendCommand("CREATE TABLE dbo.POS_MenuTable (ID INTEGER PRIMARY KEY, TYPE TEXT, PRODUCT TEXT, SOLD INTEGER, PRICE INTEGER);");
                if (init2_res != "")
                {
                    Console.WriteLine("[SQLite] {0}", init1_res);
                }
            }
            else
            {
                Console.WriteLine("[SQL_DBInit] Error! Cannot initialize SQLite Database, SQL Connection Type is set to 'RemoteSQL'!");
            }
        }
        public static string SendCommand(string DBCommand) //Send Query
        {
            if (Program.ServerMode != 0)
            {
                OleDbConnection Connection = ConnectToDB();
                string CommandRes = "";
                try
                {
                    using (Connection)
                    {
                        Connection.Open();
                        OleDbCommand Command = new OleDbCommand(DBCommand, Connection);
                        OleDbDataReader Reader = Command.ExecuteReader();
                        if (Reader.HasRows)
                        {
                            string add = "";
                            while (Reader.Read())
                            {
                                if (CommandRes != "") { add = CommandRes + "\n"; }
                                CommandRes = add + Reader.GetValue(0).ToString();
                            }
                        }
                        Reader.Close();
                        Connection.Close();
                    }
                }
                catch (Exception e)
                {
                    StreamWriter FFS = File.AppendText("log.txt");
                    FFS.WriteLine("\n[" + DateTime.Now.ToString() + "][ERROR] " + e.Message + " " + e.StackTrace);
                    FFS.Close();
                    if (e.GetBaseException() != null) e = e.GetBaseException();
                    Console.WriteLine("[ERROR] Exception: {0}.\n[ERROR] Please see log.txt for details.", e.Message);
                    CommandRes = "";
                }
                return CommandRes;

            }
            else
            {
                string CommandRes = "";
                //Server Mode 0
                try
                {
                    SQLiteConnection SQLConn = new SQLiteConnection("Data Source=localdatabase.sqlite;Version=3;");
                    SQLConn.Open();
                    SQLiteCommand SQLComm = new SQLiteCommand("ATTACH DATABASE 'posdatabase.sqlite' AS 'dbo'; " + DBCommand, SQLConn);
                    SQLiteDataReader SQLReader = SQLComm.ExecuteReader();
                    while (SQLReader.HasRows)
                    {
                        string add = "";
                        while (SQLReader.Read())
                        {
                            if (CommandRes != "") { add = CommandRes + "\n"; }
                            CommandRes = add + SQLReader.GetValue(0).ToString();
                        }
                    }
                    SQLConn.Close();
                }
                catch (Exception e)
                {
                    StreamWriter FFS = File.AppendText("log.txt");
                    FFS.WriteLine("\n[" + DateTime.Now.ToString() + "][ERROR] " + e.Message + " " + e.StackTrace);
                    FFS.Close();
                    if (e.GetBaseException() != null) e = e.GetBaseException();
                    Console.WriteLine("[ERROR] Exception: {0}.\n[ERROR] Please see log.txt for details.", e.Message);
                    CommandRes = "";
                }
                return CommandRes;
            }

        }
        public static IDictionary<int, object> GetListFromSQL(string DBCommand, int DataType)
        {
            IDictionary<int, object> Result = new Dictionary<int, object>();
            if (Program.ServerMode != 0)
            {
                //Server Mode 1 (RemoteSQL)
                OleDbConnection Connection = Database.ConnectToDB();
                if ((DataType > -1) && (DataType < 5))
                {
                    try
                    {
                        using (Connection)
                        {
                            Connection.Open();
                            OleDbCommand Command = new OleDbCommand(DBCommand, Connection);
                            OleDbDataReader Reader = Command.ExecuteReader();
                            if (Reader.HasRows)
                            {
                                int i = 0;
                                object data = null;
                                while (Reader.Read())
                                {
                                    switch (DataType)
                                    {
                                        case DataType_String:
                                            data = Reader.GetString(0);
                                            break;
                                        case DataType_Int32:
                                            data = Reader.GetInt32(0);
                                            break;
                                        case DataType_Int16:
                                            data = Reader.GetInt16(0);
                                            break;
                                        case DataType_DateTime:
                                            data = Reader.GetDateTime(0);
                                            break;
                                        case DataType_Bool:
                                            data = Reader.GetBoolean(0);
                                            break;
                                    }
                                    if (data != null)
                                    {
                                        Result.Add(i, data.ToString());
                                    }
                                    else
                                    {
                                        Console.WriteLine("[WARNING] DATA FROM LIST IS NULL!");
                                    }
                                    i++;
                                }
                            }
                            Reader.Close();
                            Connection.Close();
                        }
                    }
                    catch (Exception e)
                    {
                        StreamWriter FFS = File.AppendText("log.txt");
                        FFS.WriteLine("\n[" + DateTime.Now.ToString() + "][ERROR] " + e.Message + " " + e.StackTrace);
                        FFS.Close();
                    }
                }
            }
            else
            {
                //Server Mode 0
                try
                {
                    SQLiteConnection SQLConn = new SQLiteConnection("Data Source=localdatabase.sqlite;Version=3;");
                    SQLConn.Open();
                    SQLiteCommand SQLComm = new SQLiteCommand("ATTACH DATABASE 'posdatabase.sqlite' AS 'dbo'; " + DBCommand, SQLConn);
                    SQLiteDataReader SQLReader = SQLComm.ExecuteReader();
                    while (SQLReader.HasRows)
                    {
                        object data = null;
                        int i = 0;
                        while (SQLReader.Read())
                        {
                            switch (DataType)
                            {
                                case DataType_String:
                                    data = SQLReader.GetString(0);
                                    break;
                                case DataType_Int32:
                                    data = SQLReader.GetInt32(0);
                                    break;
                                case DataType_Int16:
                                    data = SQLReader.GetInt16(0);
                                    break;
                                case DataType_DateTime:
                                    data = SQLReader.GetDateTime(0);
                                    break;
                                case DataType_Bool:
                                    data = SQLReader.GetBoolean(0);
                                    break;
                            }
                            if (data != null)
                            {
                                Result.Add(i, data.ToString());
                            }
                            else
                            {
                                Console.WriteLine("[WARNING] DATA FROM LIST IS NULL!");
                            }
                            i++;
                        }
                    }
                    SQLConn.Close();
                }
                catch (Exception e)
                {
                    StreamWriter FFS = File.AppendText("log.txt");
                    FFS.WriteLine("\n[" + DateTime.Now.ToString() + "][ERROR] " + e.Message + " " + e.StackTrace);
                    FFS.Close();
                    if (e.GetBaseException() != null) e = e.GetBaseException();
                    Console.WriteLine("[ERROR] Exception: {0}.\n[ERROR] Please see log.txt for details.", e.Message);
                }
            }
            return Result;
        }
        public static object GetDataFromSQL(string DBCommand, int DataType)
        {
            object Result = null;
            if (Program.ServerMode != 0)
            {
                //Server Mode 1 (RemoteSQL)
                OleDbConnection Connection = Database.ConnectToDB();
                if ((DataType > -1) && (DataType < 5))
                {
                    try
                    {
                        using (Connection)
                        {
                            Connection.Open();
                            OleDbCommand Command = new OleDbCommand(DBCommand, Connection);
                            OleDbDataReader Reader = Command.ExecuteReader();
                            if (Reader.HasRows)
                            {
                                object data = null;
                                while (Reader.Read())
                                {
                                    switch (DataType)
                                    {
                                        case DataType_String:
                                            data = Reader.GetString(0);
                                            break;
                                        case DataType_Int32:
                                            data = Reader.GetInt32(0);
                                            break;
                                        case DataType_Int16:
                                            data = Reader.GetInt16(0);
                                            break;
                                        case DataType_DateTime:
                                            data = Reader.GetDateTime(0);
                                            break;
                                        case DataType_Bool:
                                            data = Reader.GetBoolean(0);
                                            break;
                                    }
                                    if (data != null)
                                    {
                                        Result = data;
                                    }
                                    else
                                    {
                                        Console.WriteLine("[WARNING] DATA FROM LIST IS NULL!");
                                    }
                                }
                            }
                            Reader.Close();
                            Connection.Close();
                        }
                    }
                    catch (Exception e)
                    {
                        StreamWriter FFS = File.AppendText("log.txt");
                        FFS.WriteLine("\n[" + DateTime.Now.ToString() + "][ERROR] " + e.Message + " " + e.StackTrace);
                        FFS.Close();
                    }
                }
            }
            else
            {
                //Server Mode 0
                try
                {
                    SQLiteConnection SQLConn = new SQLiteConnection("Data Source=localdatabase.sqlite;Version=3;");
                    SQLConn.Open();
                    SQLiteCommand SQLComm = new SQLiteCommand("ATTACH DATABASE 'posdatabase.sqlite' AS 'dbo'; " + DBCommand, SQLConn);
                    SQLiteDataReader SQLReader = SQLComm.ExecuteReader();
                    while (SQLReader.HasRows)
                    {
                        object data = null;
                        while (SQLReader.Read())
                        {
                            switch (DataType)
                            {
                                case DataType_String:
                                    data = SQLReader.GetString(0);
                                    break;
                                case DataType_Int32:
                                    data = SQLReader.GetInt32(0);
                                    break;
                                case DataType_Int16:
                                    data = SQLReader.GetInt16(0);
                                    break;
                                case DataType_DateTime:
                                    data = SQLReader.GetDateTime(0);
                                    break;
                                case DataType_Bool:
                                    data = SQLReader.GetBoolean(0);
                                    break;
                            }
                            if (data != null)
                            {
                                Result = data;
                            }
                            else
                            {
                                Console.WriteLine("[WARNING] DATA FROM LIST IS NULL!");
                            }
                        }
                    }
                    SQLConn.Close();
                }
                catch (Exception e)
                {
                    StreamWriter FFS = File.AppendText("log.txt");
                    FFS.WriteLine("\n[" + DateTime.Now.ToString() + "][ERROR] " + e.Message + " " + e.StackTrace);
                    FFS.Close();
                    if (e.GetBaseException() != null) e = e.GetBaseException();
                    Console.WriteLine("[ERROR] Exception: {0}.\n[ERROR] Please see log.txt for details.", e.Message);
                }
            }
            return Result;
        }

    }
    #region Faulty Encryption Classes
    /// <summary>
    /// A simple RSA Asymmetric Encryption Class
    /// </summary>
    public class Encry
    {
        #region
        private string _PrivateKey;
        private string _PublicKey;
        private RSACryptoServiceProvider RSA;
        bool CanDecrypt, Initialized = false;
        #endregion
        /// <summary>
        /// Creates an instance of the Encry class.
        /// </summary>
        public Encry() { }
        /// <summary>
        /// Creates an instance of the Encry class.
        /// </summary>
        /// <param name="Initialize">Initializes the Encry Instance.</param>
        public Encry(bool Initialize)
        {
            if (Initialize)
            {
                this.Init();
            }
        }
        /// <summary>
        /// Gets the public key used by this instance.
        /// </summary>
        /// <returns>XML Public Key</returns>
        public string GetPublicKey()
        {
            if (Initialized)
            {
                return this._PublicKey;
            }
            else
            {
                throw new Exception("Encry object not yet initialized.\nInitialize it with Encry.Init().");
            }
        }
        /// <summary>
        /// Initializes this instance.
        /// </summary>
        public void Init()
        {
            if (!Initialized)
            {
                RSA = new RSACryptoServiceProvider();
                this._PrivateKey = RSA.ToXmlString(true);
                this._PublicKey = RSA.ToXmlString(false);
                this.Initialized = true;
                this.CanDecrypt = true;
            }
        }
        /// <summary>
        /// Initializes this instance with a public key for decrypting only.
        /// </summary>
        /// <param name="PublicKey">The public key to use.</param>
        public void Init(string PublicKey)
        {
            if (!Initialized)
            {
                RSA = new RSACryptoServiceProvider();
                //_PrivateKey = RSA.ToXmlString(true);
                RSA.FromXmlString(PublicKey);
                this._PublicKey = RSA.ToXmlString(false);
                this.Initialized = true;
                this.CanDecrypt = false;
            }
        }
        /// <summary>
        /// Encrypts data using the keys stored by this instance.
        /// </summary>
        /// <param name="Data">The data to encrypt.</param>
        /// <returns>The encrypted data.</returns>
        public byte[] Encrypt(byte[] Data)
        {
            byte[] ret;
            ret = RSA.Encrypt(Data, true);
            return ret;
        }
        /// <summary>
        /// Decrypts data using the keys stored by this instance.
        /// </summary>
        /// <param name="Data">The data to decrypt.</param>
        /// <returns>The decrypted data.</returns>
        public byte[] Decrypt(byte[] Data)
        {
            byte[] ret;
            byte[] EnData = Data;
            if (this.CanDecrypt)
            {
                if (EnData.Length > 128)
                {
                    EnData = Tools.DeleteRestArray(Data, 128);
                }
                RSA.FromXmlString(this._PrivateKey);
                //Console.WriteLine("\n\n{0}\n\n", this._PublicKey);
                ret = RSA.Decrypt(EnData, true);
            }
            else
            {
                ret = null;
            }
            return ret;
        }
    }

    /// <summary>
    /// A simple AES Symmetric Encryption Class
    /// </summary>
    public class SEncry
    {
        #region
        private SymmetricAlgorithm _aes;
        private byte[] _key, _IV;
        #endregion
        /// <summary>
        /// Creates an instance of the SEncry class.
        /// </summary>
        public SEncry()
        {
            this._aes = new AesManaged();
            this._key = this._aes.Key;
            this._IV = this._aes.IV;
        }
        /// <summary>
        /// Creates an instance of the SEncry class.
        /// </summary>
        /// <param name="Key">The symmetric key used for decrypting.</param>
        public SEncry(byte[] Key)
        {
            this._aes = new AesManaged
            {
                Key = Key
            };
            this._key = this._aes.Key;
            this._IV = this._aes.IV;
        }
        /// <summary>
        /// Creates an instance of the SEncry class.
        /// </summary>
        /// <param name="Key">The symmetric key used for encrypting and decrypting.</param>
        /// <param name="IV">The Initialization Vector used for encrypting.</param>
        public SEncry(byte[] Key, byte[] IV)
        {
            this._aes = new AesManaged
            {
                Key = Key,
                IV = IV
            };
            this._key = this._aes.Key;
            this._IV = this._aes.IV;
        }

        /// <summary>
        /// Gets the symmetric key used by this instance.
        /// </summary>
        /// <returns>The symmetric key.</returns>
        public byte[] GetKey()
        {
            return this._key;
        }
        /// <summary>
        /// Gets the Initialization Vector used by this instance.
        /// </summary>
        /// <returns>The initialization vector.</returns>
        public byte[] GetIV()
        {
            return this._IV;
        }
        /// <summary>
        /// Encrypts data using AES Encryption.
        /// </summary>
        /// <param name="Data">The data to encrypt.</param>
        /// <returns>The encrypted data.</returns>
        public byte[] Encrypt(byte[] Data)
        {
            byte[] ret = null;
            ICryptoTransform Ecr = this._aes.CreateEncryptor(this._key, this._aes.IV);
            using (MemoryStream MemStm = new MemoryStream())
            {
                using (CryptoStream CryStm = new CryptoStream(MemStm, Ecr, CryptoStreamMode.Write))
                {
                    CryStm.Write(Data, 0, Data.Length);
                    CryStm.FlushFinalBlock();
                }
                ret = MemStm.ToArray();
            }
            return ret;
        }
        /// <summary>
        /// Decrypts data using AES Encryption
        /// </summary>
        /// <param name="Data">The data to decrypt.</param>
        /// <returns>The decrypted data.</returns>
        public byte[] Decrypt(byte[] Data)
        {
            byte[] ret = null;
            ICryptoTransform Dcr = this._aes.CreateDecryptor(this._key, this._aes.IV);
            using (MemoryStream MemStm = new MemoryStream(Data))
            {
                using (CryptoStream CryStm = new CryptoStream(MemStm, Dcr, CryptoStreamMode.Read))
                {
                    int b;
                    List<byte> bdata = new List<byte>();
                    while ((b = CryStm.ReadByte()) != -1)
                    {
                        bdata.Add((byte)b);
                    }
                    ret = bdata.ToArray();
                }
            }
            return ret;
        }
        /// <summary>
        /// Generates a new random initialization vector.
        /// </summary>
        public void GenerateIV()
        {
            this._aes.GenerateIV();
            this._IV = this._aes.IV;
        }
        /// <summary>
        /// Generates a new random key.
        /// </summary>
        public void GenerateKey()
        {
            this._aes.GenerateKey();
            this._key = this._aes.Key;
        }
    }
    #endregion
}
/*
 * ██╗  ██╗███████╗██╗     ██████╗     ███╗   ███╗███████╗
 * ██║  ██║██╔════╝██║     ██╔══██╗    ████╗ ████║██╔════╝
 * ███████║█████╗  ██║     ██████╔╝    ██╔████╔██║█████╗  
 * ██╔══██║██╔══╝  ██║     ██╔═══╝     ██║╚██╔╝██║██╔══╝  
 * ██║  ██║███████╗███████╗██║         ██║ ╚═╝ ██║███████╗
 * ╚═╝  ╚═╝╚══════╝╚══════╝╚═╝         ╚═╝     ╚═╝╚══════╝
 *///Send Help
