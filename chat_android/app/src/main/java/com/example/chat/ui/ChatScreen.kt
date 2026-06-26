package com.example.chat.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Send
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.example.chat.model.BlePeer
import com.example.chat.model.WifiDirectPeer
import com.example.chat.ui.theme.*
import com.example.chat.viewmodel.ChatViewModel
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatScreen(viewModel: ChatViewModel = viewModel()) {
    val drawerState = rememberDrawerState(initialValue = DrawerValue.Closed)
    val scope = rememberCoroutineScope()

    ModalNavigationDrawer(
        drawerState = drawerState,
        drawerContent = {
            ModalDrawerSheet(
                drawerContainerColor = DiscordSidebar,
                modifier = Modifier.width(300.dp)
            ) {
                DrawerContent(viewModel = viewModel)
            }
        }
    ) {
        Scaffold(
            topBar = {
                TopAppBar(
                    title = {
                        val isConnected by viewModel.isConnected.collectAsState()
                        Column {
                            Text("P2P Chat", fontWeight = FontWeight.Bold, color = DiscordTextPrimary)
                            Text(
                                if (isConnected) "Connected" else "Disconnected",
                                fontSize = 12.sp,
                                color = if (isConnected) DiscordSuccess else DiscordDanger
                            )
                        }
                    },
                    navigationIcon = {
                        IconButton(onClick = { scope.launch { drawerState.open() } }) {
                            Icon(Icons.Default.Menu, contentDescription = "Menu", tint = DiscordTextPrimary)
                        }
                    },
                    colors = TopAppBarDefaults.topAppBarColors(
                        containerColor = DiscordBackground
                    )
                )
            },
            containerColor = DiscordBackground
        ) { paddingValues ->
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(paddingValues)
            ) {
                ChatArea(viewModel = viewModel, modifier = Modifier.weight(1f))
                MessageInput(viewModel = viewModel)
            }
        }
    }
}

@Composable
fun DrawerContent(viewModel: ChatViewModel) {
    val publisherStatus by viewModel.publisherStatus.collectAsState()
    val watcherStatus by viewModel.watcherStatus.collectAsState()
    val wifiStatus by viewModel.wifiStatus.collectAsState()
    val isConnected by viewModel.isConnected.collectAsState()

    val blePeers by viewModel.blePeers.collectAsState()
    val wifiPeers by viewModel.peers.collectAsState()
    val errors by viewModel.errors.collectAsState()

    Column(modifier = Modifier.fillMaxSize()) {
        // Header
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .background(DiscordDarkBackground)
                .padding(16.dp)
        ) {
            Column {
                Text("Device: ${viewModel.localSession.sessionId.toString().take(8)}", color = DiscordTextPrimary, fontWeight = FontWeight.Bold)
            }
        }

        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            item {
                Text("STATUS", fontSize = 12.sp, fontWeight = FontWeight.Bold, color = DiscordTextSecondary)
                Text(publisherStatus, color = DiscordTextMuted, fontSize = 12.sp)
                Text(watcherStatus, color = DiscordTextMuted, fontSize = 12.sp)
                Text(wifiStatus, color = DiscordTextMuted, fontSize = 12.sp)

                if (errors != null) {
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(errors!!, color = DiscordDanger, fontSize = 12.sp)
                }
            }

            item {
                Divider(color = DiscordDarkBackground)
                Spacer(modifier = Modifier.height(8.dp))
                Text("CONTROLS", fontSize = 12.sp, fontWeight = FontWeight.Bold, color = DiscordTextSecondary)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(top = 8.dp)) {
                    Button(onClick = { viewModel.startAdvertising() }, colors = ButtonDefaults.buttonColors(containerColor = DiscordBlurple)) { Text("Adv", fontSize = 12.sp) }
                    Button(onClick = { viewModel.startScanning() }, colors = ButtonDefaults.buttonColors(containerColor = DiscordBlurple)) { Text("Scan", fontSize = 12.sp) }
                    Button(onClick = { viewModel.startWifi() }, colors = ButtonDefaults.buttonColors(containerColor = DiscordBlurple)) { Text("WFD", fontSize = 12.sp) }
                }
                if (isConnected) {
                    Button(
                        onClick = { viewModel.disconnect() },
                        colors = ButtonDefaults.buttonColors(containerColor = DiscordDanger),
                        modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
                    ) {
                        Text("Disconnect")
                    }
                }
            }

            item {
                Divider(color = DiscordDarkBackground)
                Spacer(modifier = Modifier.height(8.dp))
                Text("WIFI DIRECT PEERS (${wifiPeers.size})", fontSize = 12.sp, fontWeight = FontWeight.Bold, color = DiscordTextSecondary)
            }

            items(wifiPeers) { peer ->
                PeerItem(
                    name = peer.deviceName,
                    address = peer.deviceAddress,
                    onClick = { viewModel.connectToPeer(peer) }
                )
            }

            item {
                Divider(color = DiscordDarkBackground)
                Spacer(modifier = Modifier.height(8.dp))
                Text("BLE PEERS (${blePeers.size})", fontSize = 12.sp, fontWeight = FontWeight.Bold, color = DiscordTextSecondary)
            }

            items(blePeers) { peer ->
                PeerItem(
                    name = peer.pcName,
                    address = peer.macAddress,
                    onClick = { }
                )
            }
        }
    }
}

@Composable
fun PeerItem(name: String, address: String, onClick: () -> Unit) {
    Surface(
        color = DiscordDarkBackground,
        shape = RoundedCornerShape(8.dp),
        onClick = onClick,
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(modifier = Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(
                modifier = Modifier.size(40.dp).clip(CircleShape).background(DiscordBlurple),
                contentAlignment = Alignment.Center
            ) {
                Text(name.take(1).uppercase(), color = Color.White, fontWeight = FontWeight.Bold)
            }
            Spacer(modifier = Modifier.width(12.dp))
            Column {
                Text(name, color = DiscordTextPrimary, fontWeight = FontWeight.Bold)
                Text(address, color = DiscordTextMuted, fontSize = 12.sp)
            }
        }
    }
}

@Composable
fun ChatArea(viewModel: ChatViewModel, modifier: Modifier = Modifier) {
    val messages by viewModel.messages.collectAsState()

    LazyColumn(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp),
        reverseLayout = false // In real app you might want true, but keeping simple
    ) {
        items(messages) { msg ->
            if (msg.isMe) {
                // Right aligned
                Row(modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = Arrangement.End) {
                    Box(
                        modifier = Modifier
                            .background(DiscordChatBubbleMe, RoundedCornerShape(16.dp, 16.dp, 4.dp, 16.dp))
                            .padding(12.dp)
                    ) {
                        Column {
                            Text(msg.message, color = Color.White)
                            Text(msg.timestamp, color = Color.White.copy(alpha = 0.7f), fontSize = 10.sp, modifier = Modifier.align(Alignment.End))
                        }
                    }
                }
            } else {
                // Left aligned
                Row(modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = Arrangement.Start) {
                    Box(
                        modifier = Modifier.size(36.dp).clip(CircleShape).background(DiscordBlurple),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(msg.senderName.take(1).uppercase(), color = Color.White, fontWeight = FontWeight.Bold)
                    }
                    Spacer(modifier = Modifier.width(8.dp))
                    Box(
                        modifier = Modifier
                            .background(DiscordChatBubblePeer, RoundedCornerShape(16.dp, 16.dp, 16.dp, 4.dp))
                            .padding(12.dp)
                    ) {
                        Column {
                            Text(msg.senderName, color = DiscordBlurple, fontWeight = FontWeight.Bold, fontSize = 12.sp)
                            Text(msg.message, color = DiscordTextPrimary)
                            Text(msg.timestamp, color = DiscordTextMuted, fontSize = 10.sp, modifier = Modifier.align(Alignment.End))
                        }
                    }
                }
            }
        }
    }
}

@Composable
fun MessageInput(viewModel: ChatViewModel) {
    var text by remember { mutableStateOf("") }
    val isConnected by viewModel.isConnected.collectAsState()

    Surface(
        color = DiscordDarkBackground,
        modifier = Modifier.padding(16.dp),
        shape = RoundedCornerShape(24.dp)
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            TextField(
                value = text,
                onValueChange = { text = it },
                modifier = Modifier.weight(1f),
                placeholder = { Text("Message...", color = DiscordTextMuted) },
                colors = TextFieldDefaults.colors(
                    focusedContainerColor = Color.Transparent,
                    unfocusedContainerColor = Color.Transparent,
                    focusedIndicatorColor = Color.Transparent,
                    unfocusedIndicatorColor = Color.Transparent,
                    focusedTextColor = DiscordTextPrimary,
                    unfocusedTextColor = DiscordTextPrimary
                ),
                enabled = isConnected
            )
            IconButton(
                onClick = {
                    if (text.isNotBlank()) {
                        viewModel.sendMessage(text)
                        text = ""
                    }
                },
                enabled = isConnected && text.isNotBlank()
            ) {
                Box(
                    modifier = Modifier.size(40.dp).clip(CircleShape).background(DiscordBlurple),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(Icons.Default.Send, contentDescription = "Send", tint = Color.White, modifier = Modifier.size(20.dp))
                }
            }
        }
    }
}
