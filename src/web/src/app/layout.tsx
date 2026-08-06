import type { Metadata } from "next";
import { Inter, Geist_Mono } from "next/font/google";
import "./globals.css";
import { launchModeBootstrapScript } from "@hosty-sdk/app";
import { HostLaunchBridge } from "@hosty-sdk/app/react";
import { Providers } from "@/components/providers";
import { HostThemeBridge } from "@/components/host-theme-bridge";
import { AppShell } from "@/components/app-shell";

// Inter for everything the app renders — chrome, data-dense console, and media titles alike; Geist
// Mono for codecs / ids / paths.
const inter = Inter({
  variable: "--font-inter",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: "Media Server",
  description: "Torrent ingest, automatic organize/identify/probe, and Jellyfin-compatible streaming.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html
      lang="en"
      suppressHydrationWarning
      className={`${inter.variable} ${geistMono.variable} h-full antialiased`}
    >
      <head>
        {/* Ahead of any body markup, so chrome a shell already renders is never painted. */}
        <script dangerouslySetInnerHTML={{ __html: launchModeBootstrapScript }} />
      </head>
      <body className="bg-background text-foreground min-h-full">
        <HostThemeBridge />
        <HostLaunchBridge />
        <Providers>
          <AppShell>{children}</AppShell>
        </Providers>
      </body>
    </html>
  );
}
