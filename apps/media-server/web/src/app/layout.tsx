import type { Metadata } from "next";
import { Inter, Fraunces, Geist_Mono } from "next/font/google";
import "./globals.css";
import { Providers } from "@/components/providers";
import { HostThemeBridge } from "@/components/host-theme-bridge";
import { AppShell } from "@/components/app-shell";

// Inter for the app chrome + data-dense console (matches the Hosty Shell); Fraunces, a characterful
// serif, for media titles only ("content speaks in serif, the app speaks in sans"); Geist Mono for
// codecs / ids / paths.
const inter = Inter({
  variable: "--font-inter",
  subsets: ["latin"],
});

const fraunces = Fraunces({
  variable: "--font-fraunces",
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
      className={`${inter.variable} ${fraunces.variable} ${geistMono.variable} h-full antialiased`}
    >
      <body className="bg-background text-foreground min-h-full">
        <HostThemeBridge />
        <Providers>
          <AppShell>{children}</AppShell>
        </Providers>
      </body>
    </html>
  );
}
