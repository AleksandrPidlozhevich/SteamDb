# SteamDb

## Description

This application helps you keep track of completed, in-progress, and not-started games.  
It is designed for people who purchase many games during sales but then forget about some of them.  
With SteamDb, you can track your gaming progress and better manage your backlog.

---

## Download

- [⬇️ Windows](https://github.com/AleksandrPidlozhevich/SteamDb/releases)  
- [⬇️ MacOS](https://github.com/AleksandrPidlozhevich/SteamDb/releases)  


---

## How to find your Steam ID

1. Open Steam and click your **username** in the top-right corner.
2. Select **Account details** from the dropdown.
3. At the top of the window, you'll see your Steam display name in large font.
   Below that is your **Steam ID** — a long number (usually 17 digits).
4. This number is the one required by the application.

📷 _Example screenshot:_  
![Steam ID example](imagesReadme/SteamID.PNG)

---

## How to get Notion API Integration Token

To connect the app with your Notion workspace, follow these steps:

1. Go to the [Notion integrations page](https://www.notion.so/profile/integrations).
2. Click **"New integration"**.
3. Fill in the name and select the workspace where the integration will be used.
4. Once created, copy the **Internal Integration Token** — it looks like a long string starting with `secret_...`.
5. Save this token somewhere safe — you'll need it in the app settings.

🛡️ Make sure to grant the following permissions to the integration:

- ✅ `Read content`
- ✅ `Update content`
- ✅ `Insert content`

📌 Then, **share the desired Notion pages or databases** with the integration (like sharing to a person), so it can access them.

---
## How to find your Notion Database ID

To connect the app to a specific Notion database, you need its unique ID.

1. Open your Notion workspace and navigate to the target database (table, board, list, etc.).
2. Look at the URL in your browser.
3. The **Database ID** is the part right after the last slash and before the question mark `?`.  
It is a 32-character alphanumeric string.

📘 Reference: [Official Notion Docs — Retrieve a Database](https://developers.notion.com/reference/retrieve-a-database)

📷 _Example screenshot:_  
![Database ID Example](imagesReadme/notion_database_id.png)
 
 ---
 ## Google Sheets Integration (Beta)

Authorization with Google Sheets is currently in progress.

- The app uses standard Google OAuth for access.
- In some cases, the authentication flow may not work correctly yet.
- Integration is in **beta**, so some features may be unstable or unavailable.

🔧 We’re working on improving this — thank you for your patience!

## Main Features

---

## License

MIT (or your actual license)

---

## Feedback and Issues
aleksandr.pidlozhevich@gmail.com
